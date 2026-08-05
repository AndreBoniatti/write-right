using WriteRight.Api.Llm;
using WriteRight.Shared.Analysis;
using WriteRight.Shared.Corrections;
using WriteRight.Shared.Exercises;

namespace WriteRight.Tests.Support;

/// <summary>
/// <see cref="ILlmProvider"/> de teste: devolve resultados pré-configurados (sem
/// tocar rede nem gastar crédito de IA) e registra a última requisição recebida.
/// Deixa o <c>PracticeService</c> ser testado isolado da chamada real ao Claude.
///
/// O consumo devolvido é configurável (<see cref="Usage"/>) pra os testes poderem
/// afirmar que o custo foi registrado — sem isso, dava pra quebrar a instrumentação
/// inteira sem nenhum teste reclamar.
/// </summary>
public sealed class StubLlmProvider : ILlmProvider
{
    /// <summary>
    /// Consumo padrão de toda chamada. Modelo real e COM preço em tabela, de
    /// propósito: assim o custo calculado é &gt; 0 e o teste distingue "registrado"
    /// de "não registrado".
    /// </summary>
    public static readonly LlmUsage DefaultUsage = new("claude-sonnet-5", 1_000, 500, 0, 0);

    private readonly GeneratedExercise? _exercise;
    private readonly CorrectionResult? _correction;
    private readonly AnalysisDraft? _analysis;

    public StubLlmProvider(
        GeneratedExercise? exercise = null,
        CorrectionResult? correction = null,
        AnalysisDraft? analysis = null)
    {
        _exercise = exercise;
        _correction = correction;
        _analysis = analysis;
    }

    /// <summary>Consumo que toda chamada devolve. Sobrescrevível por teste.</summary>
    public LlmUsage Usage { get; set; } = DefaultUsage;

    /// <summary>
    /// Simula o caso "a API respondeu e cobrou, mas a resposta não virou resultado"
    /// (recusa, JSON truncado por estourar o teto). Toda chamada passa a lançar
    /// <see cref="LlmCallFailedException"/> carregando o <see cref="Usage"/>.
    /// </summary>
    public bool FailAfterBilling { get; set; }

    /// <summary>
    /// Simula o navegador desistindo NO MEIO da chamada: a fonte é cancelada durante
    /// a execução, como o <c>RequestAborted</c> faz quando o <c>HttpClient</c> do
    /// Blazor estoura o timeout. A chamada em si completa — do lado da Anthropic ela
    /// completa mesmo — e o que se testa é se o gasto ainda é persistido.
    /// </summary>
    public CancellationTokenSource? CancelDuringCall { get; set; }

    private LlmCallFailedException Failure() => new(
        Usage, "stub: falha após a cobrança", new InvalidOperationException("json truncado"));

    public CorrectionRequest? LastCorrectionRequest { get; private set; }
    public ExerciseGenerationRequest? LastGenerationRequest { get; private set; }
    public AnalysisRequest? LastAnalysisRequest { get; private set; }

    public Task<LlmResult<GeneratedExercise>> GenerateExerciseAsync(
        ExerciseGenerationRequest request, CancellationToken ct = default)
    {
        LastGenerationRequest = request;
        CancelDuringCall?.Cancel();
        if (FailAfterBilling) throw Failure();
        return Task.FromResult(new LlmResult<GeneratedExercise>(
            _exercise ?? throw new InvalidOperationException("StubLlmProvider sem exercício configurado."),
            Usage));
    }

    public Task<LlmResult<CorrectionResult>> CorrectAsync(
        CorrectionRequest request, CancellationToken ct = default)
    {
        LastCorrectionRequest = request;
        CancelDuringCall?.Cancel();
        if (FailAfterBilling) throw Failure();
        return Task.FromResult(new LlmResult<CorrectionResult>(
            _correction ?? throw new InvalidOperationException("StubLlmProvider sem correção configurada."),
            Usage));
    }

    public Task<LlmResult<AnalysisDraft>> AnalyzeAsync(
        AnalysisRequest request, CancellationToken ct = default)
    {
        LastAnalysisRequest = request;
        CancelDuringCall?.Cancel();
        if (FailAfterBilling) throw Failure();
        return Task.FromResult(new LlmResult<AnalysisDraft>(
            _analysis ?? throw new InvalidOperationException("StubLlmProvider sem análise configurada."),
            Usage));
    }
}
