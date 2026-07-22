using WriteRight.Shared.Corrections;
using WriteRight.Shared.Exercises;

namespace WriteRight.Api.Llm;

/// <summary>
/// Costura pro provedor de IA. Hoje só existe a implementação Anthropic; a
/// interface está aqui pra não acoplar o resto do app a um provedor específico
/// (e é o ponto de extensão se um dia virar produto multi-provedor).
///
/// Decisão de arquitetura: modelo é configurável (ver <see cref="LlmOptions"/>),
/// mas provedor NÃO é dinâmico via front — seria over-engineering pro uso pessoal.
/// </summary>
public interface ILlmProvider
{
    /// <summary>Gera um texto pro usuário traduzir, conforme os parâmetros.</summary>
    Task<GeneratedExercise> GenerateExerciseAsync(
        ExerciseGenerationRequest request, CancellationToken ct = default);

    /// <summary>
    /// Corrige a tradução do usuário, devolvendo erros categorizados via
    /// structured output (schema derivado da taxonomia).
    /// </summary>
    Task<CorrectionResult> CorrectAsync(
        CorrectionRequest request, CancellationToken ct = default);
}
