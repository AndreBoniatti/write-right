using WriteRight.Shared;

namespace WriteRight.Api.Data;

/// <summary>
/// Entidade de persistência: um exercício que o usuário fez (texto gerado +
/// tradução + correção). É o registro histórico que alimenta o perfil.
///
/// Separada dos DTOs do <c>Shared</c> de propósito: DTO é contrato imutável
/// (record), entidade tem Id e navegação e vive só no backend.
/// </summary>
public class ExerciseAttempt
{
    public int Id { get; set; }

    public Language SourceLanguage { get; set; }
    public Language TargetLanguage { get; set; }
    public CefrLevel? Level { get; set; }
    public string? Theme { get; set; }

    public string SourceText { get; set; } = "";
    public string UserTranslation { get; set; } = "";
    public string CorrectedText { get; set; } = "";
    public string OverallComment { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<ExerciseError> Errors { get; set; } = new();
}
