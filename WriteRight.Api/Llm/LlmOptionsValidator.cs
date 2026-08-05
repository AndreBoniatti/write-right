using Microsoft.Extensions.Options;

namespace WriteRight.Api.Llm;

/// <summary>
/// Valida <see cref="LlmOptions"/> no startup (<c>ValidateOnStart</c>).
///
/// Existe porque as tarifas moram só no appsettings, sem fallback no código: sem
/// esta trava, esquecer um preço não quebraria nada visível — a app subiria, as
/// práticas funcionariam, e só o relatório de custo viria vazio. Erro de dinheiro
/// que não aparece é o pior tipo, então ele vira falha de boot.
///
/// A <see cref="LlmOptions.ApiKey"/> NÃO é validada aqui de propósito: ela falha com
/// mensagem amigável no primeiro uso, e exigi-la no boot impediria a app de subir só
/// pra ler o histórico.
/// </summary>
public sealed class LlmOptionsValidator : IValidateOptions<LlmOptions>
{
    public ValidateOptionsResult Validate(string? name, LlmOptions options)
    {
        var problems = new List<string>();

        // Mesmo comparador do LlmPricing. Se aqui fosse case-sensitive, uma tarifa
        // grafada com maiúscula passaria na validação e devolveria custo nulo depois —
        // exatamente o buraco que esta classe existe pra fechar.
        var priced = new HashSet<string>(options.Pricing.Keys, StringComparer.OrdinalIgnoreCase);

        foreach (var (model, rate) in options.Pricing)
        {
            // Negativo é erro de digitação — jamais uma tarifa. Zero é permitido: é
            // alguém afirmando que aquele modelo não custa.
            if (rate.InputPerMTok < 0 || rate.OutputPerMTok < 0)
                problems.Add($"Llm:Pricing:{model} tem tarifa negativa.");
        }

        // Todo modelo EM USO precisa de tarifa. É o que garante que o relatório de
        // custo nasce completo em vez de meio vazio.
        foreach (var (setting, model) in InUse(options))
        {
            if (string.IsNullOrWhiteSpace(model))
                problems.Add($"Llm:{setting} está vazio.");
            else if (!priced.Contains(model))
                problems.Add(
                    $"Llm:{setting} usa '{model}', que não tem tarifa em Llm:Pricing. " +
                    $"Adicione: \"{model}\": {{ \"InputPerMTok\": 0.00, \"OutputPerMTok\": 0.00 }}");
        }

        return problems.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(problems);
    }

    private static IEnumerable<(string Setting, string Model)> InUse(LlmOptions options)
    {
        yield return (nameof(LlmOptions.GenerationModel), options.GenerationModel);
        yield return (nameof(LlmOptions.CorrectionModel), options.CorrectionModel);
        yield return (nameof(LlmOptions.AnalysisModel), options.AnalysisModel);
    }
}
