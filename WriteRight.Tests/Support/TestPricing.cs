using Microsoft.Extensions.Options;
using WriteRight.Api.Llm;

namespace WriteRight.Tests.Support;

/// <summary>
/// Constrói o <see cref="LlmPricing"/> pros testes.
///
/// Preço vem só do appsettings em produção, então aqui há uma tabela de FIXTURE —
/// ela não é a de produção nem tenta espelhá-la. Os testes de serviço se importam
/// com "o custo foi registrado", não com quanto o Sonnet custa; amarrar a tabela real
/// faria toda alteração de preço quebrar testes que não têm nada a ver com preço.
/// </summary>
public static class TestPricing
{
    /// <summary>Tarifas de fixture cobrindo os modelos que os testes usam.</summary>
    public static LlmPricing Default() => With(
        ("claude-haiku-4-5", 1m, 5m),
        ("claude-sonnet-5", 3m, 15m));

    public static LlmPricing With(params (string Model, decimal Input, decimal Output)[] rates)
    {
        var options = new LlmOptions();
        foreach (var (model, input, output) in rates)
            options.Pricing[model] = new ModelRate { InputPerMTok = input, OutputPerMTok = output };
        return From(options);
    }

    public static LlmPricing From(LlmOptions options) => new(Options.Create(options));
}
