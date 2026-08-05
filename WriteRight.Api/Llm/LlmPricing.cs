using Microsoft.Extensions.Options;

namespace WriteRight.Api.Llm;

/// <summary>
/// Converte tokens em dólares.
///
/// A tabela existe porque a API <b>não devolve custo</b>: o bloco <c>usage</c> da
/// Messages API só traz contagem de token, e a Models API não expõe preço. Traduzir
/// token em dinheiro é sempre responsabilidade do cliente. (A Usage &amp; Cost Admin
/// API devolve USD de verdade, mas é agregada por organização e com latência — serve
/// pra CONFERIR este cálculo depois, não pra substituí-lo.)
///
/// As tarifas vêm SÓ de <c>Llm:Pricing</c> no appsettings — não há tabela embutida
/// no código. Preço muda com mais frequência que código, e ter os valores em dois
/// lugares só cria a dúvida de qual está valendo. Como não existe fallback,
/// <see cref="LlmOptionsValidator"/> derruba a app no startup se algum modelo em uso
/// estiver sem tarifa: falha barulhenta na hora de subir, em vez de relatório de
/// custo silenciosamente vazio semanas depois.
/// </summary>
public sealed class LlmPricing
{
    /// <summary>Multiplicador da GRAVAÇÃO no cache de prompt sobre o preço de entrada (TTL padrão de 5 min; o de 1h é 2×).</summary>
    private const decimal CacheWriteMultiplier = 1.25m;

    /// <summary>Multiplicador da LEITURA do cache sobre o preço de entrada.</summary>
    private const decimal CacheReadMultiplier = 0.10m;

    private readonly IReadOnlyDictionary<string, ModelRate> _rates;

    public LlmPricing(IOptions<LlmOptions> options)
    {
        // Recopia com comparador case-insensitive (o binder de config não garante
        // isso): o nome do modelo vem da RESPOSTA da API, e não vale um relatório
        // errado por causa de maiúscula.
        _rates = new Dictionary<string, ModelRate>(options.Value.Pricing, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Custo em USD da chamada, ou <c>null</c> se o modelo não tem tarifa configurada.
    /// Null e não zero: zero silencioso viraria relatório mentindo pra baixo. O
    /// validador impede que isso aconteça com os modelos EM USO, mas uma resposta pode
    /// vir de um modelo inesperado (alias que resolveu pra outro snapshot) — aí o
    /// registro guarda os tokens e o custo entra como <c>unpricedCalls</c>.
    /// </summary>
    public decimal? CostOf(LlmUsage usage)
    {
        if (!TryGetRate(usage.Model, out var rate)) return null;

        var input = usage.InputTokens * rate.InputPerMTok
                  + usage.CacheWriteTokens * rate.InputPerMTok * CacheWriteMultiplier
                  + usage.CacheReadTokens * rate.InputPerMTok * CacheReadMultiplier;

        var output = usage.OutputTokens * rate.OutputPerMTok;

        return (input + output) / 1_000_000m;
    }

    /// <summary>
    /// Busca a tarifa aceitando tanto o alias quanto o snapshot datado.
    ///
    /// O que se grava é o modelo da RESPOSTA — o que foi cobrado de verdade — e um
    /// alias resolve pra um snapshot: pedir <c>claude-haiku-4-5</c> devolve
    /// <c>claude-haiku-4-5-20251001</c>. Sem esta queda pro alias, configurar o preço
    /// do alias (a única coisa que dá pra fazer, já que o snapshot só aparece em
    /// runtime) nunca casaria, e toda chamada viraria custo nulo.
    ///
    /// Exato tem precedência: dá pra precificar um snapshot específico se um dia a
    /// tarifa dele divergir da do alias.
    /// </summary>
    private bool TryGetRate(string model, out ModelRate rate)
    {
        if (_rates.TryGetValue(model, out rate!)) return true;

        var alias = AliasOf(model);
        return alias is not null && _rates.TryGetValue(alias, out rate!);
    }

    /// <summary>
    /// Tira o sufixo de snapshot <c>-AAAAMMDD</c>, ou <c>null</c> se não houver.
    /// Só oito dígitos no fim contam — assim <c>claude-sonnet-4-6</c> e
    /// <c>claude-opus-4-8</c> não são confundidos com data e ficam intactos.
    /// </summary>
    private static string? AliasOf(string model)
    {
        const int suffix = 9; // '-' + AAAAMMDD
        if (model.Length <= suffix) return null;

        var dash = model.Length - suffix;
        if (model[dash] != '-') return null;

        for (var i = dash + 1; i < model.Length; i++)
            if (!char.IsAsciiDigit(model[i])) return null;

        return model[..dash];
    }
}
