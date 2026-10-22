using WriteRight.Shared.Analysis;
using WriteRight.Shared.Cards;
using WriteRight.Shared.Corrections;
using WriteRight.Shared.Exercises;

namespace WriteRight.Api.Llm;

/// <summary>
/// Costura pro provedor de IA: o resto do app não conhece a Anthropic, e os testes
/// trocam isto por um stub. Todo método devolve <see cref="LlmResult{T}"/> — o
/// resultado E o que ele consumiu.
/// </summary>
public interface ILlmProvider
{
    /// <summary>Gera um texto pro usuário traduzir, conforme os parâmetros.</summary>
    Task<LlmResult<GeneratedExercise>> GenerateExerciseAsync(
        ExerciseGenerationRequest request, CancellationToken ct = default);

    /// <summary>
    /// Corrige a tradução do usuário, devolvendo erros categorizados via
    /// structured output (schema derivado da taxonomia).
    /// </summary>
    Task<LlmResult<CorrectionResult>> CorrectAsync(
        CorrectionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Analisa o histórico de erros e devolve os padrões por trás deles. A saída é
    /// <b>crua</b> (evidência por id): quem confere os ids contra o que foi enviado
    /// é o serviço, não o provider.
    /// </summary>
    Task<LlmResult<AnalysisDraft>> AnalyzeAsync(
        AnalysisRequest request, CancellationToken ct = default);

    /// <summary>
    /// Decide, erro a erro, o que vira card de vocabulário e desenha o card. A saída é
    /// <b>proposta</b>: quem confere contra o texto (resposta literal, dica literal,
    /// tamanho) é o <c>CardGate</c>, não o provider.
    /// </summary>
    Task<LlmResult<CardDesign>> DesignCardsAsync(
        CardDesignRequest request, CancellationToken ct = default);
}
