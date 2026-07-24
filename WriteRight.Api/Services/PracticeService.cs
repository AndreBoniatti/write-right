using Microsoft.EntityFrameworkCore;
using WriteRight.Api.Data;
using WriteRight.Api.Llm;
using WriteRight.Shared.Corrections;
using WriteRight.Shared.Exercises;
using WriteRight.Shared.Practices;
using WriteRight.Shared.Profile;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Api.Services;

/// <summary>Desfecho de uma operação que muda uma prática existente.</summary>
public enum PracticeOutcome
{
    Ok,
    NotFound,
    /// <summary>A prática está concluída (somente leitura) e não aceita mais mudanças.</summary>
    ReadOnly,
}

/// <summary>
/// Orquestra o ciclo de vida da prática: cria (gera + persiste como InProgress),
/// lista, retoma, salva rascunho, corrige (→ Completed, readonly), exclui, e monta
/// o perfil de fraquezas. Mantém o <see cref="ILlmProvider"/> focado só na IA.
/// </summary>
public sealed class PracticeService
{
    private const int PreviewLength = 120;
    private const int FocusCategoryCount = 3;

    private readonly ILlmProvider _llm;
    private readonly WriteRightDbContext _db;

    public PracticeService(ILlmProvider llm, WriteRightDbContext db)
    {
        _llm = llm;
        _db = db;
    }

    /// <summary>
    /// Cria uma prática: gera o texto e persiste como <see cref="PracticeStatus.InProgress"/>.
    /// Se <see cref="CreatePracticeRequest.FocusOnWeaknesses"/>, mira a geração nas
    /// categorias mais frequentes do perfil (direcionamento adaptativo no servidor).
    /// </summary>
    public async Task<PracticeDetail> CreatePracticeAsync(CreatePracticeRequest request, CancellationToken ct = default)
    {
        if (request.SourceLanguage == request.TargetLanguage)
            throw new ArgumentException(
                "Origem e alvo devem ser idiomas diferentes.", nameof(request));

        IReadOnlyList<ErrorCategory>? focus = null;
        if (request.FocusOnWeaknesses)
        {
            var profile = await GetProfileAsync(ct);
            if (profile.ByCategory.Count > 0)
                focus = profile.ByCategory.Take(FocusCategoryCount).Select(c => c.Category).ToList();
        }

        var generated = await _llm.GenerateExerciseAsync(
            new ExerciseGenerationRequest(
                request.SourceLanguage, request.TargetLanguage, request.WordCount,
                request.Level, request.Theme, focus),
            ct);

        var practice = new ExerciseAttempt
        {
            Status = PracticeStatus.InProgress,
            SourceLanguage = request.SourceLanguage,
            TargetLanguage = request.TargetLanguage,
            Level = request.Level,
            Theme = request.Theme,
            SourceText = generated.SourceText,
            UserTranslation = "",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _db.Exercises.Add(practice);
        await _db.SaveChangesAsync(ct);
        return ToDetail(practice);
    }

    /// <summary>Listagem da tela inicial: resumos leves, da mais recente pra mais antiga.</summary>
    public async Task<IReadOnlyList<PracticeSummary>> ListPracticesAsync(CancellationToken ct = default)
    {
        // Projeta os campos necessários (ErrorCount vira subquery COUNT). O preview
        // é truncado e a ordenação é feita EM MEMÓRIA: o SQLite não traduz nem
        // Substring nem ORDER BY sobre DateTimeOffset. (Volume pessoal — custo irrelevante.)
        var rows = await _db.Exercises
            .Select(p => new
            {
                p.Id,
                p.SourceLanguage,
                p.TargetLanguage,
                p.Level,
                p.Theme,
                p.Status,
                p.SourceText,
                ErrorCount = p.Errors.Count,
                p.CreatedAt,
            })
            .ToListAsync(ct);

        return rows
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new PracticeSummary(
                r.Id, r.SourceLanguage, r.TargetLanguage, r.Level, r.Theme, r.Status,
                Preview(r.SourceText), r.ErrorCount, r.CreatedAt))
            .ToList();
    }

    /// <summary>Detalhe completo de uma prática (retomar ou ler). Null se não existe.</summary>
    public async Task<PracticeDetail?> GetPracticeAsync(int id, CancellationToken ct = default)
    {
        var practice = await _db.Exercises
            .Include(p => p.Errors)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        return practice is null ? null : ToDetail(practice);
    }

    /// <summary>Salva o rascunho da tradução ("Salvar e sair") sem corrigir.</summary>
    public async Task<PracticeOutcome> SaveDraftAsync(int id, string userTranslation, CancellationToken ct = default)
    {
        var practice = await _db.Exercises.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (practice is null) return PracticeOutcome.NotFound;
        if (practice.Status == PracticeStatus.Completed) return PracticeOutcome.ReadOnly;

        practice.UserTranslation = userTranslation;
        await _db.SaveChangesAsync(ct);
        return PracticeOutcome.Ok;
    }

    /// <summary>
    /// Corrige a prática: chama a IA, persiste os erros na MESMA tentativa e a marca
    /// como <see cref="PracticeStatus.Completed"/> (readonly). Idempotência barata:
    /// uma prática já concluída volta <see cref="PracticeOutcome.ReadOnly"/>.
    /// </summary>
    public async Task<(PracticeOutcome Outcome, PracticeDetail? Detail)> CorrectPracticeAsync(
        int id, string userTranslation, CancellationToken ct = default)
    {
        var practice = await _db.Exercises
            .Include(p => p.Errors)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (practice is null) return (PracticeOutcome.NotFound, null);
        if (practice.Status == PracticeStatus.Completed) return (PracticeOutcome.ReadOnly, null);

        var result = await _llm.CorrectAsync(
            new CorrectionRequest(
                practice.SourceLanguage, practice.TargetLanguage,
                practice.SourceText, userTranslation, practice.Level, practice.Theme),
            ct);

        practice.UserTranslation = userTranslation;
        practice.CorrectedText = result.CorrectedText;
        practice.OverallComment = result.OverallComment;
        practice.Errors = result.Errors.Select(e => new ExerciseError
        {
            Category = e.Category,
            Severity = e.Severity,
            Original = e.Original,
            Correction = e.Correction,
            Explanation = e.Explanation,
        }).ToList();
        practice.Status = PracticeStatus.Completed;
        practice.CompletedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        return (PracticeOutcome.Ok, ToDetail(practice));
    }

    /// <summary>Exclui uma prática (e seus erros, por cascade). Permitido em qualquer status.</summary>
    public async Task<PracticeOutcome> DeletePracticeAsync(int id, CancellationToken ct = default)
    {
        var practice = await _db.Exercises.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (practice is null) return PracticeOutcome.NotFound;

        _db.Exercises.Remove(practice);
        await _db.SaveChangesAsync(ct);
        return PracticeOutcome.Ok;
    }

    /// <summary>
    /// Perfil de fraquezas: agrega os erros por categoria. Só práticas CONCLUÍDAS
    /// entram na contagem (as em andamento ainda não têm erros, por construção).
    /// </summary>
    public async Task<ErrorProfile> GetProfileAsync(CancellationToken ct = default)
    {
        // Agrega em memória (dados pessoais, volume baixo) — evita depender da
        // tradução de GroupBy sobre a coluna enum convertida em string.
        var categories = await _db.Errors.Select(e => e.Category).ToListAsync(ct);

        var byCategory = categories
            .GroupBy(c => c)
            .Select(g => new CategoryCount(g.Key, g.Count()))
            .OrderByDescending(c => c.Count)
            .ToList();

        var totalAttempts = await _db.Exercises
            .CountAsync(p => p.Status == PracticeStatus.Completed, ct);

        return new ErrorProfile(totalAttempts, byCategory.Sum(c => c.Count), byCategory);
    }

    private static string Preview(string sourceText) =>
        sourceText.Length <= PreviewLength ? sourceText : sourceText[..PreviewLength];

    private static PracticeDetail ToDetail(ExerciseAttempt p)
    {
        var completed = p.Status == PracticeStatus.Completed;
        return new PracticeDetail(
            p.Id, p.SourceLanguage, p.TargetLanguage, p.Level, p.Theme, p.Status,
            p.SourceText, p.UserTranslation,
            completed ? p.CorrectedText : null,
            completed ? p.OverallComment : null,
            p.Errors.Select(e => new WritingError(
                e.Category, e.Severity, e.Original, e.Correction, e.Explanation)).ToList(),
            p.CreatedAt, p.CompletedAt);
    }
}
