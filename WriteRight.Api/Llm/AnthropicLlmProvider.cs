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
    /// <summary>
    /// Effort da correção e da análise (Sonnet 5). <c>High</c> é o default do modelo — está aqui
    /// EXPLÍCITO justamente por isso: default é decisão do PROVEDOR, e depender dele
    /// significa que uma mudança do lado da Anthropic altera o custo e o comportamento
    /// do app sem passar por um commit. Effort é a alavanca de custo mais forte que
    /// existe aqui; ela não pode ser implícita.
    ///
    /// NÃO vai na geração: o Haiku 4.5 não suporta o parâmetro (a chamada erraria).
    ///
    /// <b>Medium</b>, em avaliação desde 2026-08-30. Medido em 14 correções reais:
    /// custa ~37% menos que <c>High</c> e responde em metade do tempo (14s contra 25s),
    /// com a mesma cobertura de erros — 6,8 contra 6,6 por prática. O <c>High</c> gasta
    /// o excedente deliberando à toa: num texto de 8 palavras queimou 3.432 tokens de
    /// saída pra achar 2 erros.
    ///
    /// O Medium arquiva mais erro gramatical em categoria de vocabulário ("the schedule
    /// → my schedule" como WordChoice). Isso era um risco pro deck enquanto a categoria
    /// decidia o que virava card; deixou de ser quando a cunhagem ganhou passo próprio
    /// (<see cref="CardEffort"/>), que julga o erro em si. Sobra o efeito no perfil, que
    /// é ruído pequeno numa agregação.
    ///
    /// O que observar no uso: severidade fora do lugar (ela diverge ~30% entre execuções
    /// em QUALQUER effort — não é sintoma de Medium). Voltar pra High é trocar esta linha.
    /// </summary>
    private static readonly Effort ReasoningEffort = Effort.Medium;

    /// <summary>
    /// Effort do desenho de cards: <b>High</b>, e no Opus 5.5 (cujo default é Medium —
    /// mais um motivo pra não depender de default). Ao contrário da correção, aqui a
    /// pergunta central é "existe outra resposta igualmente certa?", um julgamento
    /// contra si mesmo, que é onde deliberar rende.
    ///
    /// Medido em 2026-10-07 sobre 24 práticas: o Opus 5.5 em High pensou ~900 tokens
    /// por prática contra ~3.000 do Sonnet 5 em High — saiu MAIS BARATO (US$ 0,025
    /// contra 0,033), seis vezes mais rápido (~10 s) e muito mais preciso (o Sonnet
    /// deixou passar 37% dos cards ruins; o Opus, 17%).
    /// </summary>
    private static readonly Effort CardEffort = Effort.High;

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
                Effort = ReasoningEffort,
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
                Effort = ReasoningEffort,
            },
        });

        var usage = UsageOf(response, _options.AnalysisModel);

        var draft = Interpret(response, usage, "a análise", json =>
            JsonSerializer.Deserialize<AnalysisDraft>(json, LlmJson.Options)
            ?? throw new InvalidOperationException("Falha ao desserializar a análise da IA."));

        return new LlmResult<AnalysisDraft>(draft, usage);
    }

    public async Task<LlmResult<CardDesign>> DesignCardsAsync(
        CardDesignRequest request, CancellationToken ct = default)
    {
        var client = CreateClient();

        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = _options.CardModel,
            MaxTokens = 16000,
            System = CardPrompt.BuildSystemPrompt(),
            Messages = [new() { Role = Role.User, Content = CardPrompt.BuildUserMessage(request) }],
            OutputConfig = new OutputConfig
            {
                Format = new JsonOutputFormat { Schema = CardPrompt.BuildResultSchema() },
                Effort = CardEffort,
            },
        });

        var usage = UsageOf(response, _options.CardModel);

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
