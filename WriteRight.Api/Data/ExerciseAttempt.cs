using WriteRight.Shared;
using WriteRight.Shared.Practices;

namespace WriteRight.Api.Data;

/// <summary>
/// Entidade de persistência: uma prática que o usuário fez (texto gerado +
/// tradução + correção).
///
/// Ciclo de vida via <see cref="Status"/>: nasce <c>InProgress</c> na geração
/// (texto salvo, tradução/correção ainda vazias) e vira <c>Completed</c> na
/// correção (readonly).
/// </summary>
public class ExerciseAttempt
{
    public int Id { get; set; }

    public PracticeStatus Status { get; set; } = PracticeStatus.InProgress;

    public Language SourceLanguage { get; set; }
    public Language TargetLanguage { get; set; }
    public CefrLevel? Level { get; set; }
    public string? Theme { get; set; }

    public string SourceText { get; set; } = "";
    public string UserTranslation { get; set; } = "";
    public string CorrectedText { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Cunhagem dos cards desta prática. Ver <see cref="Data.CardsStatus"/>.</summary>
    public CardsStatus? CardsStatus { get; set; }

    /// <summary>Quando foi excluída; null = ativa. Ver <c>DeletePracticeAsync</c>.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    public List<ExerciseError> Errors { get; set; } = new();
}
