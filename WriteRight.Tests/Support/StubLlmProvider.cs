using WriteRight.Api.Llm;
using WriteRight.Shared.Corrections;
using WriteRight.Shared.Exercises;

namespace WriteRight.Tests.Support;

/// <summary>
/// <see cref="ILlmProvider"/> de teste: devolve resultados pré-configurados (sem
/// tocar rede nem gastar crédito de IA) e registra a última requisição recebida.
/// Deixa o <c>PracticeService</c> ser testado isolado da chamada real ao Claude.
/// </summary>
public sealed class StubLlmProvider : ILlmProvider
{
    private readonly GeneratedExercise? _exercise;
    private readonly CorrectionResult? _correction;

    public StubLlmProvider(GeneratedExercise? exercise = null, CorrectionResult? correction = null)
    {
        _exercise = exercise;
        _correction = correction;
    }

    public CorrectionRequest? LastCorrectionRequest { get; private set; }
    public ExerciseGenerationRequest? LastGenerationRequest { get; private set; }

    public Task<GeneratedExercise> GenerateExerciseAsync(
        ExerciseGenerationRequest request, CancellationToken ct = default)
    {
        LastGenerationRequest = request;
        return Task.FromResult(_exercise
            ?? throw new InvalidOperationException("StubLlmProvider sem exercício configurado."));
    }

    public Task<CorrectionResult> CorrectAsync(
        CorrectionRequest request, CancellationToken ct = default)
    {
        LastCorrectionRequest = request;
        return Task.FromResult(_correction
            ?? throw new InvalidOperationException("StubLlmProvider sem correção configurada."));
    }
}
