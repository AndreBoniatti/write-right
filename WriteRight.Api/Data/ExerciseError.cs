using WriteRight.Shared.Taxonomy;

namespace WriteRight.Api.Data;

/// <summary>
/// Entidade de persistência de um erro. É o que se agrega pra montar o perfil
/// de fraquezas (GROUP BY Category / Severity).
///
/// <see cref="Category"/> e <see cref="Severity"/> são gravados como STRING
/// (ver <see cref="WriteRightDbContext"/>) pra os dados ficarem legíveis e
/// resistirem a reordenação dos enums.
/// </summary>
public class ExerciseError
{
    public int Id { get; set; }

    public int ExerciseAttemptId { get; set; }
    public ExerciseAttempt? ExerciseAttempt { get; set; }

    public ErrorCategory Category { get; set; }
    public ErrorSeverity Severity { get; set; }

    public string Original { get; set; } = "";
    public string Correction { get; set; } = "";
    public string Explanation { get; set; } = "";

    /// <summary>
    /// Trecho do texto de origem correspondente, no idioma de origem do exercício.
    /// Insumo do card de vocabulário (a dica da frente). NULL quando não há
    /// correspondência — e também nos erros gravados antes deste campo existir,
    /// que é o que o backfill preenche.
    /// </summary>
    public string? SourcePhrase { get; set; }
}
