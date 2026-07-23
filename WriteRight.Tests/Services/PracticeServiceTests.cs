using Microsoft.EntityFrameworkCore;
using WriteRight.Api.Services;
using WriteRight.Shared;
using WriteRight.Shared.Corrections;
using WriteRight.Shared.Exercises;
using WriteRight.Shared.Taxonomy;
using WriteRight.Tests.Support;

namespace WriteRight.Tests.Services;

/// <summary>
/// O coração do loop adaptativo: corrigir+persistir e agregar o perfil de
/// fraquezas. Testado contra SQLite real (value-converters enum-as-string de
/// verdade) com um <see cref="StubLlmProvider"/> — sem rede nem custo de IA.
/// </summary>
public sealed class PracticeServiceTests : IDisposable
{
    private readonly TestDatabase _db = new();

    private static CorrectionRequest SampleRequest() => new(
        Language.Portuguese, Language.English,
        SourceText: "Eu tenho um carro.",
        UserTranslation: "I have car.",
        Level: CefrLevel.B1,
        Theme: "cotidiano");

    private static CorrectionResult CorrectionWith(params ErrorCategory[] categories)
    {
        var errors = categories
            .Select(c => new WritingError(c, ErrorSeverity.Understandable, "x", "y", "porquê"))
            .ToList();
        return new CorrectionResult("I have a car.", errors, "Quase lá!");
    }

    private PracticeService NewService(
        CorrectionResult? correction = null, GeneratedExercise? exercise = null) =>
        new(new StubLlmProvider(exercise, correction), _db.NewContext());

    [Fact]
    public async Task CorrectAndSaveAsync_persists_attempt_and_errors()
    {
        var correction = CorrectionWith(ErrorCategory.Article, ErrorCategory.WordChoice);

        var returned = await NewService(correction).CorrectAndSaveAsync(SampleRequest());

        Assert.Same(correction, returned); // devolve o resultado da IA intacto

        // Persistiu de fato — lê com um contexto novo (não o cache do EF).
        await using var ctx = _db.NewContext();
        var attempt = ctx.Exercises.Include(e => e.Errors).Single();

        Assert.Equal(Language.Portuguese, attempt.SourceLanguage);
        Assert.Equal(Language.English, attempt.TargetLanguage);
        Assert.Equal(CefrLevel.B1, attempt.Level);
        Assert.Equal("cotidiano", attempt.Theme);
        Assert.Equal("Eu tenho um carro.", attempt.SourceText);
        Assert.Equal("I have car.", attempt.UserTranslation);
        Assert.Equal("I have a car.", attempt.CorrectedText);
        Assert.Equal("Quase lá!", attempt.OverallComment);

        Assert.Equal(2, attempt.Errors.Count);
        Assert.Contains(attempt.Errors, e => e.Category == ErrorCategory.Article);
        Assert.Contains(attempt.Errors, e => e.Category == ErrorCategory.WordChoice);
    }

    [Fact]
    public async Task CorrectAndSaveAsync_with_no_errors_saves_attempt_only()
    {
        await NewService(new CorrectionResult("perfect", new List<WritingError>(), "Perfeito!"))
            .CorrectAndSaveAsync(SampleRequest());

        await using var ctx = _db.NewContext();
        Assert.Equal(1, ctx.Exercises.Count());
        Assert.Equal(0, ctx.Errors.Count());
    }

    [Fact]
    public async Task GetProfileAsync_on_empty_db_is_all_zero()
    {
        var profile = await NewService().GetProfileAsync();

        Assert.Equal(0, profile.TotalAttempts);
        Assert.Equal(0, profile.TotalErrors);
        Assert.Empty(profile.ByCategory);
    }

    [Fact]
    public async Task GetProfileAsync_aggregates_and_orders_by_frequency()
    {
        // Tentativa 1: WordChoice ×2, Spelling ×1
        await NewService(CorrectionWith(
                ErrorCategory.WordChoice, ErrorCategory.WordChoice, ErrorCategory.Spelling))
            .CorrectAndSaveAsync(SampleRequest());

        // Tentativa 2: WordChoice ×1, VerbTense ×2
        await NewService(CorrectionWith(
                ErrorCategory.WordChoice, ErrorCategory.VerbTense, ErrorCategory.VerbTense))
            .CorrectAndSaveAsync(SampleRequest());

        var profile = await NewService().GetProfileAsync();

        Assert.Equal(2, profile.TotalAttempts);
        Assert.Equal(6, profile.TotalErrors);

        // Ordenado da fraqueza mais frequente pra menos: WordChoice(3) > VerbTense(2) > Spelling(1).
        Assert.Collection(profile.ByCategory,
            c => { Assert.Equal(ErrorCategory.WordChoice, c.Category); Assert.Equal(3, c.Count); },
            c => { Assert.Equal(ErrorCategory.VerbTense, c.Category); Assert.Equal(2, c.Count); },
            c => { Assert.Equal(ErrorCategory.Spelling, c.Category); Assert.Equal(1, c.Count); });
    }

    [Fact]
    public async Task GenerateAsync_delegates_to_provider_without_persisting()
    {
        var exercise = new GeneratedExercise(
            Language.Portuguese, Language.English, "Um texto.", CefrLevel.A2, Theme: null);

        var result = await NewService(exercise: exercise)
            .GenerateAsync(new ExerciseGenerationRequest(
                Language.Portuguese, Language.English, 40, CefrLevel.A2));

        Assert.Same(exercise, result);

        // Geração não cria tentativa — só a correção persiste.
        await using var ctx = _db.NewContext();
        Assert.Equal(0, ctx.Exercises.Count());
    }

    public void Dispose() => _db.Dispose();
}
