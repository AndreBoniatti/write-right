using System.Diagnostics;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;
using WriteRight.Shared.Analysis;
using WriteRight.Shared.Cards;
using WriteRight.Shared.Corrections;
using WriteRight.Shared.Exercises;

namespace WriteRight.Api.Llm;

/// <summary>
/// Implementação Anthropic da <see cref="ILlmProvider"/> — chama o Claude com
/// structured output em todas as operações.
///
/// <b>O <c>ct</c> NÃO é repassado ao SDK, de propósito.</b> Ele é o
/// <c>RequestAborted</c> da requisição, cancelado assim que o navegador desiste.
/// Abortar a chamada ali não devolveria o dinheiro (a geração já está em curso do lado
/// da Anthropic) e ainda destruiria o bloco de <c>usage</c> da resposta. Deixar
/// completar custa o mesmo e mantém o resultado (ver <c>UsageService.AfterBilling</c>).
/// </summary>
public sealed class AnthropicLlmProvider : ILlmProvider
{
    /// <summary>
    /// Effort da correção e da análise. EXPLÍCITO mesmo quando coincide com o default:
    /// default é decisão do provedor, e depender dele deixaria uma mudança do lado da
    /// Anthropic alterar custo e comportamento do app sem passar por um commit.
    ///
    /// <b>Medium</b> porque, nas correções reais, custou bem menos que <c>High</c> e
    /// respondeu na metade do tempo com a mesma cobertura de erros. NÃO vai na geração:
    /// o Haiku 4.5 não suporta o parâmetro.
    /// </summary>
    private static readonly Effort ReasoningEffort = Effort.Medium;

    /// <summary>
    /// Effort do desenho de cards: <b>High</b>. Aqui a pergunta central é "existe outra
    /// resposta igualmente certa?", um julgamento contra si mesmo, que é onde deliberar
    /// rende.
    /// </summary>
    private static readonly Effort CardEffort = Effort.High;

    /// <summary>
    /// Teto de saída (pensamento + JSON) da correção, da análise e dos cards. O teto em
    /// si não custa nada — cobra-se o que é gerado —, então fica com folga larga sobre
    /// o maior uso visto.
    ///
    /// Não passa de 16 mil porque as chamadas são sem streaming, e esse é o teto que o
    /// SDK recomenda pra resposta caber no timeout HTTP. Timeout é o pior desfecho: a
    /// chamada é cobrada e a resposta se perde. Precisar de mais é sinal pra trocar pra
    /// streaming, não pra subir este número.
    /// </summary>
    private const int MaxOutputTokens = 16_000;

    /// <summary>
    /// A geração não pensa (Haiku 4.5, sem effort) e o texto tem no máximo
    /// <c>PracticeLimits.MaxWords</c> palavras — algumas centenas de tokens. O teto baixo
    /// aqui é trava, não aperto: um modelo que desandasse a escrever pararia logo.
    /// </summary>
    private const int GenerationMaxTokens = 2_000;

    private readonly LlmOptions _options;
    private readonly Lazy<AnthropicClient> _client;

    public AnthropicLlmProvider(IOptions<LlmOptions> options)
    {
        _options = options.Value;
        _client = new(CreateClient);
    }

    public async Task<LlmResult<GeneratedExercise>> GenerateExerciseAsync(
        ExerciseGenerationRequest request, CancellationToken ct = default)
    {
        var client = _client.Value;

        var clock = Stopwatch.StartNew();
        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = _options.GenerationModel,
            MaxTokens = GenerationMaxTokens,
            System = GenerationPrompt.BuildSystemPrompt(),
            Messages = [new() { Role = Role.User, Content = GenerationPrompt.BuildUserMessage(request) }],
            OutputConfig = new OutputConfig
            {
                Format = new JsonOutputFormat { Schema = GenerationPrompt.BuildResultSchema() },
            },
        });

        var usage = UsageOf(response, _options.GenerationModel, clock.Elapsed);

        var exercise = Interpret(response, usage, "a geração", json =>
        {
            var text = JsonSerializer.Deserialize<GeneratedText>(json, LlmJson.Options)?.Text
                ?? throw new InvalidOperationException("Falha ao desserializar o texto gerado.");
            return new GeneratedExercise(text.Trim());
        });

        return new LlmResult<GeneratedExercise>(exercise, usage);
    }

    public async Task<LlmResult<CorrectionResult>> CorrectAsync(
        CorrectionRequest request, CancellationToken ct = default)
    {
        var client = _client.Value;

        var clock = Stopwatch.StartNew();
        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = _options.CorrectionModel,
            MaxTokens = MaxOutputTokens,
            System = CorrectionPrompt.BuildSystemPrompt(),
            Messages = [new() { Role = Role.User, Content = CorrectionPrompt.BuildUserMessage(request) }],
            OutputConfig = new OutputConfig
            {
                Format = new JsonOutputFormat { Schema = CorrectionPrompt.BuildResultSchema() },
                Effort = ReasoningEffort,
            },
        });

        var usage = UsageOf(response, _options.CorrectionModel, clock.Elapsed);

        var correction = Interpret(response, usage, "a correção", json =>
            JsonSerializer.Deserialize<CorrectionResult>(json, LlmJson.Options)
            ?? throw new InvalidOperationException("Falha ao desserializar a correção da IA."));

        return new LlmResult<CorrectionResult>(correction, usage);
    }

    public async Task<LlmResult<AnalysisDraft>> AnalyzeAsync(
        AnalysisRequest request, CancellationToken ct = default)
    {
        var client = _client.Value;

        var clock = Stopwatch.StartNew();
        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = _options.AnalysisModel,
            MaxTokens = MaxOutputTokens,
            System = AnalysisPrompt.BuildSystemPrompt(request),
            Messages = [new() { Role = Role.User, Content = AnalysisPrompt.BuildUserMessage(request) }],
            OutputConfig = new OutputConfig
            {
                Format = new JsonOutputFormat { Schema = AnalysisPrompt.BuildResultSchema() },
                Effort = ReasoningEffort,
            },
        });

        var usage = UsageOf(response, _options.AnalysisModel, clock.Elapsed);

        var draft = Interpret(response, usage, "a análise", json =>
            JsonSerializer.Deserialize<AnalysisDraft>(json, LlmJson.Options)
            ?? throw new InvalidOperationException("Falha ao desserializar a análise da IA."));

        return new LlmResult<AnalysisDraft>(draft, usage);
    }

    public async Task<LlmResult<CardDesign>> DesignCardsAsync(
        CardDesignRequest request, CancellationToken ct = default)
    {
        var client = _client.Value;

        var clock = Stopwatch.StartNew();
        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = _options.CardModel,
            MaxTokens = MaxOutputTokens,
            System = CardPrompt.BuildSystemPrompt(),
            Messages = [new() { Role = Role.User, Content = CardPrompt.BuildUserMessage(request) }],
            OutputConfig = new OutputConfig
            {
                Format = new JsonOutputFormat { Schema = CardPrompt.BuildResultSchema() },
                Effort = CardEffort,
            },
        });

        var usage = UsageOf(response, _options.CardModel, clock.Elapsed);

        var design = Interpret(response, usage, "o desenho dos cards", json =>
            JsonSerializer.Deserialize<CardDesign>(json, LlmJson.Options)
            ?? throw new InvalidOperationException("Falha ao desserializar o desenho dos cards."));

        return new LlmResult<CardDesign>(design, usage);
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
                usage, $"Falha ao interpretar {what} (a chamada já foi cobrada; StopReason: {response.StopReason}).", ex);
        }
    }

    /// <summary>
    /// Lê o consumo da resposta. O modelo vem de <c>response.Model</c> (o que a API
    /// de fato cobrou), não da config — se um alias resolver pra outro snapshot, o
    /// registro reflete a cobrança real. <paramref name="requested"/> é só o
    /// fallback caso a resposta venha sem esse campo.
    ///
    /// <paramref name="elapsed"/> é cronometrado em volta do <c>Messages.Create</c>, então
    /// inclui as novas tentativas que o SDK faz sozinho (<c>MaxRetries</c>) — é o tempo
    /// que quem clicou esperou, não só o da última tentativa.
    /// </summary>
    private static LlmUsage UsageOf(Message response, string requested, TimeSpan elapsed)
    {
        // response.Model é ApiEnum<string, Model>; a atribuição explícita resolve a
        // conversão implícita (num ternário ela fica ambígua e não compila).
        string model = response.Model;
        if (string.IsNullOrWhiteSpace(model)) model = requested;

        var usage = response.Usage;
        if (usage is null) return LlmUsage.Unknown(model, elapsed);

        return new LlmUsage(
            model,
            usage.InputTokens,
            usage.OutputTokens,
            usage.CacheCreationInputTokens ?? 0,
            usage.CacheReadInputTokens ?? 0,
            elapsed);
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

    /// <summary>
    /// Um cliente só, com um <c>HttpClient</c> só, pra vida toda do app. Criado na
    /// primeira chamada: sem key, as telas que não chamam a IA continuam abrindo.
    /// </summary>
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
