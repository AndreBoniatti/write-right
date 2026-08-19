using WriteRight.Shared.Taxonomy;

namespace WriteRight.Shared.Corrections;

/// <summary>
/// Um erro pontual encontrado na tradução do usuário. É o objeto que a IA
/// devolve (via structured output) e que é persistido pra montar o perfil
/// de fraquezas.
/// </summary>
/// <param name="Category">Categoria primária (uma só; desempate = a mais específica).</param>
/// <param name="Severity">Impacto do erro.</param>
/// <param name="Original">O trecho errado, como o usuário escreveu.</param>
/// <param name="Correction">O trecho corrigido.</param>
/// <param name="Explanation">Explicação didática em português (o "porquê").</param>
/// <param name="SourcePhrase">
/// O trecho do texto de origem que gerou este erro, no IDIOMA DE ORIGEM do
/// exercício — não em português fixo: numa prática EN→PT a origem é o inglês.
/// É a dica da frente do card de vocabulário; sem ela a lacuna do cloze aceita
/// mais de uma resposta certa. Null quando não há correspondência limpa (erro de
/// ortografia, pontuação) — nesses casos o card simplesmente não nasce.
/// </param>
public sealed record WritingError(
    ErrorCategory Category,
    ErrorSeverity Severity,
    string Original,
    string Correction,
    string Explanation,
    string? SourcePhrase = null);
