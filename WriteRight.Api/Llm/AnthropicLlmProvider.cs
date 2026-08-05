using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;
using WriteRight.Shared.Analysis;
using WriteRight.Shared.Corrections;
using WriteRight.Shared.Exercises;

namespace WriteRight.Api.Llm;

/// <summary>
/// Implementação Anthropic da <see cref="ILlmProvider"/> — chama o Claude com
/// structured output tanto na geração quanto na correção.
///
/// <b>O <c>ct</c> NÃO é repassado ao SDK, de propósito.</b> Ele é o
/// <c>RequestAborted</c> da requisição, cancelado assim que o navegador desiste — e
/// o cliente Blazor desiste sozinho no timeout padrão de 100s. Abortar a chamada ali
/// não devolveria o dinheiro (a geração já está em curso do lado da Anthropic) e
/// ainda destruiria o bloco de <c>usage</c> da resposta, trocando um gasto
/// contabilizado por um gasto invisível. Deixar completar custa o mesmo e mantém o
/// resultado — que é persistido com <c>UsageService.AfterBilling</c>, então o usuário
/// o encontra ao recarregar em vez de pagar de novo.
/// </summary>
public sealed class AnthropicLlmProvider : ILlmProvider
{
    private readonly LlmOptions _options;

    public AnthropicLlmProvider(IOptions<LlmOptions> options)
    {
        _options = options.Value;
    }

    public async Task<LlmResult<GeneratedExercise>> GenerateExerciseAsync(
        ExerciseGenerationRequest request, CancellationToken ct = default)
    {
        var client = CreateClient();

        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = _options.GenerationModel,
            MaxTokens = 2000,
            System = GenerationPrompt.BuildSystemPrompt(),
            Messages = [new() { Role = Role.User, Content = GenerationPrompt.BuildUserMessage(request) }],
            OutputConfig = new OutputConfig
            {
                Format = new JsonOutputFormat { Schema = GenerationPrompt.BuildResultSchema() },
            },
        });

        var usage = UsageOf(response, _options.GenerationModel);

        var exercise = Interpret(response, usage, "a geração", json =>
        {
            var text = JsonSerializer.Deserialize<GeneratedText>(json, LlmJson.Options)?.Text
                ?? throw new InvalidOperationException("Falha ao desserializar o texto gerado.");
            return new GeneratedExercise(
                request.SourceLanguage, request.TargetLanguage, text.Trim(), request.Level, request.Theme);
        });

        return new LlmResult<GeneratedExercise>(exercise, usage);
    }

    public async Task<LlmResult<CorrectionResult>> CorrectAsync(
        CorrectionRequest request, CancellationToken ct = default)
    {
        var client = CreateClient();

        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = _options.CorrectionModel,
            MaxTokens = 16000,
            System = CorrectionPrompt.BuildSystemPrompt(),
            Messages = [new() { Role = Role.User, Content = CorrectionPrompt.BuildUserMessage(request) }],
            OutputConfig = new OutputConfig
            {
                Format = new JsonOutputFormat { Schema = CorrectionPrompt.BuildResultSchema() },
            },
        });

        var usage = UsageOf(response, _options.CorrectionModel);

        var correction = Interpret(response, usage, "a correção", json =>
            JsonSerializer.Deserialize<CorrectionResult>(json, LlmJson.Options)
            ?? throw new InvalidOperationException("Falha ao desserializar a correção da IA."));

        return new LlmResult<CorrectionResult>(correction, usage);
    }

    public async Task<LlmResult<AnalysisDraft>> AnalyzeAsync(
        AnalysisRequest request, CancellationToken ct = default)
    {
        var client = CreateClient();

        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = _options.AnalysisModel,
            MaxTokens = 8000,
            System = AnalysisPrompt.BuildSystemPrompt(request),
            Messages = [new() { Role = Role.User, Content = AnalysisPrompt.BuildUserMessage(request) }],
            OutputConfig = new OutputConfig
            {
                Format = new JsonOutputFormat { Schema = AnalysisPrompt.BuildResultSchema(request) },
            },
        });

        var usage = UsageOf(response, _options.AnalysisModel);

        var draft = Interpret(response, usage, "a análise", json =>
            JsonSerializer.Deserialize<AnalysisDraft>(json, LlmJson.Options)
            ?? throw new InvalidOperationException("Falha ao desserializar a análise da IA."));

        return new LlmResult<AnalysisDraft>(draft, usage);
    }

    /// <summary>
    /// Interpreta a resposta convertendo QUALQUER falha de leitura em
    /// <see cref="LlmCallFailedException"/>, com o consumo anexado.
    ///
    /// Neste ponto a chamada já foi cobrada: deixar a exceção subir crua perderia o
    /// gasto. E é o gasto que mais dói — bater no teto de saída significa ter pago
    /// pelos tokens todos antes de o JSON ficar impossível de desserializar.
    /// </summary>
    private static T Interpret<T>(Message response, LlmUsage usage, string what, Func<string, T> parse)
    {
        try
        {
            return parse(FirstText(response));
        }
        catch (Exception ex)
        {
            throw new LlmCallFailedException(
                usage, $"Falha ao interpretar {what} (a chamada já foi cobrada).", ex);
        }
    }

    /// <summary>
    /// Lê o consumo da resposta. O modelo vem de <c>response.Model</c> (o que a API
    /// de fato cobrou), não da config — se um alias resolver pra outro snapshot, o
    /// registro reflete a cobrança real. <paramref name="requested"/> é só o
    /// fallback caso a resposta venha sem esse campo.
    /// </summary>
    private static LlmUsage UsageOf(Message response, string requested)
    {
        // response.Model é ApiEnum<string, Model>; a atribuição explícita resolve a
        // conversão implícita (num ternário ela fica ambígua e não compila).
        string model = response.Model;
        if (string.IsNullOrWhiteSpace(model)) model = requested;

        var usage = response.Usage;
        if (usage is null) return LlmUsage.Unknown(model);

        return new LlmUsage(
            model,
            usage.InputTokens,
            usage.OutputTokens,
            usage.CacheCreationInputTokens ?? 0,
            usage.CacheReadInputTokens ?? 0);
    }

    /// <summary>Extrai o JSON do primeiro bloco de texto (structured output).</summary>
    private static string FirstText(Message response)
    {
        var text = response.Content.Select(b => b.Value).OfType<TextBlock>().FirstOrDefault()?.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException(
                $"A IA não devolveu texto (possível recusa). StopReason: {response.StopReason}");
        return text;
    }

    /// <summary>Cria o cliente Anthropic com a key configurada (validação num lugar só).</summary>
    private AnthropicClient CreateClient()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException(
                "API key da Anthropic não configurada. Rode no projeto Api: " +
                "dotnet user-secrets set \"Llm:ApiKey\" \"sk-ant-...\"");

        return new AnthropicClient { ApiKey = _options.ApiKey };
    }

    /// <summary>Forma do JSON de geração (só o texto).</summary>
    private sealed record GeneratedText(string Text);
}
