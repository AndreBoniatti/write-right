using Microsoft.Extensions.Options;

namespace WriteRight.Api.Llm;

/// <summary>
/// Converte tokens em dólares. Existe porque a API <b>não devolve custo</b>: o bloco
/// <c>usage</c> só traz contagem de token, e traduzir isso em dinheiro é do cliente.
/// As tarifas vêm de <see cref="LlmOptions.Pricing"/>.
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
    /// Busca a tarifa aceitando tanto o alias quanto o snapshot datado. O que se grava é
    /// o modelo da RESPOSTA, e um alias resolve pra um snapshot: pedir
    /// <c>claude-haiku-4-5</c> devolve <c>claude-haiku-4-5-20251001</c>. Sem esta queda
    /// pro alias, o preço configurado nunca casaria e toda chamada viraria custo nulo.
    /// Exato tem precedência.
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
