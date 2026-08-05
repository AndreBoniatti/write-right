using WriteRight.Api.Llm;
using WriteRight.Tests.Support;

namespace WriteRight.Tests.Llm;

/// <summary>
/// A tabela de preços é a única aritmética do app onde errar custa dinheiro de
/// verdade — a conta é o que vai dizer se a margem existe. Estes testes travam o que
/// dá pra errar em silêncio: os multiplicadores de cache, a separação entre os baldes
/// de token, e o modelo sem tarifa.
/// </summary>
public class LlmPricingTests
{
    private static readonly LlmPricing Pricing = TestPricing.Default();

    [Fact]
    public void CostOf_prices_input_and_output_at_the_model_rate()
    {
        // Sonnet 5 na fixture: $3 entrada / $15 saída por 1M.
        // (1000 × 3 + 500 × 15) ÷ 1.000.000 = 0,0105
        var usage = new LlmUsage("claude-sonnet-5", InputTokens: 1_000, OutputTokens: 500,
            CacheWriteTokens: 0, CacheReadTokens: 0);

        Assert.Equal(0.0105m, Pricing.CostOf(usage));
    }

    [Fact]
    public void CostOf_uses_the_per_model_rate()
    {
        // Haiku 4.5: $1 / $5. Mesmos tokens da correção custam ~1/3.
        var usage = new LlmUsage("claude-haiku-4-5", 1_000, 500, 0, 0);

        Assert.Equal(0.0035m, Pricing.CostOf(usage));
    }

    [Fact]
    public void CostOf_applies_cache_multipliers_to_the_input_rate()
    {
        // Gravar no cache custa 1,25× a entrada; ler custa 0,1×.
        // entrada: 1000×3 + 2000×3×1,25 + 4000×3×0,10 = 3000 + 7500 + 1200 = 11700
        // saída:   500×15 = 7500        → 19200 ÷ 1M = 0,0192
        var usage = new LlmUsage("claude-sonnet-5", 1_000, 500,
            CacheWriteTokens: 2_000, CacheReadTokens: 4_000);

        Assert.Equal(0.0192m, Pricing.CostOf(usage));
    }

    [Fact]
    public void CostOf_treats_the_four_token_buckets_as_disjoint()
    {
        // InputTokens já é só o resto NÃO cacheado — somar os quatro não conta nada
        // duas vezes. Se a API mudasse isso, este teste é o que quebraria.
        var split = new LlmUsage("claude-sonnet-5", 400, 0, 0, 600);
        var noCache = new LlmUsage("claude-sonnet-5", 1_000, 0, 0, 0);

        // 400×3 + 600×3×0,10 = 1200 + 180 = 1380  →  bem abaixo dos 3000 sem cache.
        Assert.Equal(0.00138m, Pricing.CostOf(split));
        Assert.Equal(0.003m, Pricing.CostOf(noCache));
        Assert.True(Pricing.CostOf(split) < Pricing.CostOf(noCache));
    }

    [Fact]
    public void CostOf_returns_null_for_a_model_without_a_configured_rate()
    {
        // Null, não zero. Zero silencioso viraria relatório mentindo pra baixo.
        // O validador impede isso pros modelos EM USO, mas uma resposta pode vir de um
        // modelo inesperado — aí os tokens ficam gravados e o custo entra como unpriced.
        var usage = new LlmUsage("claude-modelo-que-nao-existe", 1_000, 500, 0, 0);

        Assert.Null(Pricing.CostOf(usage));
    }

    [Fact]
    public void CostOf_ignores_model_name_casing()
    {
        // O nome vem da RESPOSTA da API — não vale relatório errado por maiúscula.
        Assert.Equal(
            Pricing.CostOf(new LlmUsage("claude-sonnet-5", 1_000, 500, 0, 0)),
            Pricing.CostOf(new LlmUsage("Claude-Sonnet-5", 1_000, 500, 0, 0)));
    }

    // ── Alias x snapshot datado ──────────────────────────────────────────────

    [Fact]
    public void CostOf_prices_a_dated_snapshot_using_its_alias_rate()
    {
        // Bug real, visto em produção: grava-se o modelo da RESPOSTA, e o alias
        // 'claude-haiku-4-5' volta como 'claude-haiku-4-5-20251001'. Só o alias dá pra
        // configurar — o snapshot só aparece em runtime.
        var usage = new LlmUsage("claude-haiku-4-5-20251001", 610, 53, 0, 0);

        // (610×1 + 53×5) ÷ 1M = 0,000875
        Assert.Equal(0.000875m, Pricing.CostOf(usage));
    }

    [Fact]
    public void CostOf_prefers_an_exact_snapshot_rate_over_the_alias()
    {
        // Precedência do exato: permite precificar um snapshot cuja tarifa divirja.
        var pricing = TestPricing.With(
            ("claude-haiku-4-5", 1m, 5m),
            ("claude-haiku-4-5-20251001", 2m, 10m));

        Assert.Equal(0.007m, pricing.CostOf(new LlmUsage("claude-haiku-4-5-20251001", 1_000, 500, 0, 0)));
        Assert.Equal(0.0035m, pricing.CostOf(new LlmUsage("claude-haiku-4-5", 1_000, 500, 0, 0)));
    }

    [Fact]
    public void CostOf_still_returns_null_for_an_unknown_model_with_a_date_suffix()
    {
        // A queda pro alias não pode virar rede que engole modelo desconhecido —
        // o alarme de unpricedCalls tem que continuar disparando.
        Assert.Null(Pricing.CostOf(new LlmUsage("claude-inventado-20251001", 1_000, 500, 0, 0)));
    }

    [Fact]
    public void CostOf_does_not_mistake_a_version_suffix_for_a_date()
    {
        // 'claude-sonnet-4-6' e 'claude-opus-4-8' terminam em dígito mas não em data.
        // Só oito dígitos após um hífen contam.
        var pricing = TestPricing.With(("claude-sonnet-4-6", 3m, 15m));

        Assert.Equal(0.0105m, pricing.CostOf(new LlmUsage("claude-sonnet-4-6", 1_000, 500, 0, 0)));
        Assert.Null(pricing.CostOf(new LlmUsage("claude-sonnet-4", 1_000, 500, 0, 0)));
    }

    [Fact]
    public void CostOf_honours_a_zero_rate_as_an_explicit_free_model()
    {
        // Zero configurado é alguém dizendo "este modelo não me custa" — diferente de
        // "esqueci de cadastrar", que é ausência e vira null.
        var pricing = TestPricing.With(("modelo-gratis", 0m, 0m));

        Assert.Equal(0m, pricing.CostOf(new LlmUsage("modelo-gratis", 1_000, 500, 0, 0)));
    }

    [Fact]
    public void CostOf_returns_null_for_every_model_when_nothing_is_configured()
    {
        // Sem appsettings não há preço nenhum: não existe mais tabela embutida.
        // É por isso que a app não sobe sem Llm:Pricing (ver LlmOptionsValidatorTests).
        var empty = TestPricing.From(new LlmOptions());

        Assert.Null(empty.CostOf(new LlmUsage("claude-sonnet-5", 1_000, 500, 0, 0)));
    }
}
