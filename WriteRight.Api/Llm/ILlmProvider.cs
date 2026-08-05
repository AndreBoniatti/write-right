using WriteRight.Shared.Analysis;
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
///
/// Todo método devolve <see cref="LlmResult{T}"/>: o resultado E o que ele consumiu.
/// O consumo sai por aqui, em vez de o provider gravar sozinho, porque quem sabe a
/// qual prática (ou análise) a chamada pertence é o serviço — e provider sem
/// <c>DbContext</c> continua trocável por stub sem arrastar banco pro teste.
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
}
