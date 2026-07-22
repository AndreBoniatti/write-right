using Microsoft.EntityFrameworkCore;
using WriteRight.Api.Data;
using WriteRight.Api.Llm;
using WriteRight.Shared.Corrections;
using WriteRight.Shared.Exercises;
using WriteRight.Shared.Profile;

namespace WriteRight.Api.Services;

/// <summary>
/// Orquestra o fluxo de prática: gera exercícios, corrige+persiste, e monta o
/// perfil de fraquezas. Mantém o <see cref="ILlmProvider"/> focado só na IA e a
/// persistência num lugar só.
/// </summary>
public sealed class PracticeService
{
    private readonly ILlmProvider _llm;
    private readonly WriteRightDbContext _db;

    public PracticeService(ILlmProvider llm, WriteRightDbContext db)
    {
        _llm = llm;
        _db = db;
    }

    /// <summary>Gera um texto pro usuário traduzir (não persiste — ainda não há tentativa).</summary>
    public Task<GeneratedExercise> GenerateAsync(ExerciseGenerationRequest request, CancellationToken ct = default)
        => _llm.GenerateExerciseAsync(request, ct);

    /// <summary>Corrige a tradução, persiste a tentativa + os erros, e devolve o resultado.</summary>
    public async Task<CorrectionResult> CorrectAndSaveAsync(CorrectionRequest request, CancellationToken ct = default)
    {
        var result = await _llm.CorrectAsync(request, ct);

        var attempt = new ExerciseAttempt
        {
            SourceLanguage = request.SourceLanguage,
            TargetLanguage = request.TargetLanguage,
            Level = request.Level,
            Theme = request.Theme,
            SourceText = request.SourceText,
            UserTranslation = request.UserTranslation,
            CorrectedText = result.CorrectedText,
            OverallComment = result.OverallComment,
            Errors = result.Errors.Select(e => new ExerciseError
            {
                Category = e.Category,
                Severity = e.Severity,
                Original = e.Original,
                Correction = e.Correction,
                Explanation = e.Explanation,
            }).ToList(),
        };

        _db.Exercises.Add(attempt);
        await _db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>
    /// Perfil de fraquezas: agrega todos os erros já cometidos por categoria.
    /// É o que vai direcionar a geração adaptativa (as FocusCategories).
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

        var totalAttempts = await _db.Exercises.CountAsync(ct);
        return new ErrorProfile(totalAttempts, byCategory.Sum(c => c.Count), byCategory);
    }
}
