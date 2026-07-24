using WriteRight.Api.Data;
using WriteRight.Api.Services;
using WriteRight.Shared;
using WriteRight.Shared.Corrections;
using WriteRight.Shared.Exercises;
using WriteRight.Shared.Practices;
using WriteRight.Shared.Taxonomy;
using WriteRight.Tests.Support;

namespace WriteRight.Tests.Services;

/// <summary>
/// O coração do loop adaptativo, agora com ciclo de vida: criar (gera + persiste
/// como "Em andamento"), retomar, salvar rascunho, corrigir (→ concluída, readonly),
/// excluir, e agregar o perfil. Testado contra SQLite real (value-converters de
/// verdade) com um <see cref="StubLlmProvider"/> — sem rede nem custo de IA.
/// </summary>
public sealed class PracticeServiceTests : IDisposable
{
    private readonly TestDatabase _db = new();

    // Um serviço novo por operação = um contexto novo, como em produção (scoped por request).
    private PracticeService Service(StubLlmProvider stub) => new(stub, _db.NewContext());

    private static CreatePracticeRequest CreateRequest(bool focusOnWeaknesses = false) =>
        new(Language.Portuguese, Language.English, 60, CefrLevel.B1, Theme: "cotidiano", FocusOnWeaknesses: focusOnWeaknesses);

    private static GeneratedExercise SampleExercise(string? sourceText = null) =>
        new(Language.Portuguese, Language.English, sourceText ?? "Um texto gerado.", CefrLevel.B1, Theme: "cotidiano");

    private static CorrectionResult CorrectionWith(params ErrorCategory[] categories)
    {
        var errors = categories
            .Select(c => new WritingError(c, ErrorSeverity.Understandable, "x", "y", "porquê"))
            .ToList();
        return new CorrectionResult("I have a car.", errors, "Quase lá!");
    }

    // ── Criar ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreatePracticeAsync_persists_in_progress_with_generated_text()
    {
        var detail = await Service(new StubLlmProvider(SampleExercise("Texto gerado pela IA.")))
            .CreatePracticeAsync(CreateRequest());

        Assert.Equal(PracticeStatus.InProgress, detail.Status);
        Assert.Equal("Texto gerado pela IA.", detail.SourceText);
        Assert.Equal("", detail.UserTranslation);
        Assert.Null(detail.CorrectedText);
        Assert.Null(detail.CompletedAt);
        Assert.Empty(detail.Errors);

        await using var ctx = _db.NewContext();
        Assert.Equal(1, ctx.Exercises.Count()); // agora a geração PERSISTE (era efêmera)
    }

    [Fact]
    public async Task CreatePracticeAsync_targets_top_weaknesses_when_focus_requested()
    {
        // Semeia uma prática concluída que torna WordChoice a fraqueza mais frequente.
        var seedStub = new StubLlmProvider(SampleExercise(), CorrectionWith(
            ErrorCategory.WordChoice, ErrorCategory.WordChoice, ErrorCategory.Spelling));
        var seed = await Service(seedStub).CreatePracticeAsync(CreateRequest());
        await Service(seedStub).CorrectPracticeAsync(seed.Id, "tradução");

        // Cria uma nova pedindo pra focar nas fraquezas → o servidor resolve as categorias.
        var focusStub = new StubLlmProvider(SampleExercise());
        await Service(focusStub).CreatePracticeAsync(CreateRequest(focusOnWeaknesses: true));

        var focus = focusStub.LastGenerationRequest!.FocusCategories;
        Assert.NotNull(focus);
        Assert.Equal(ErrorCategory.WordChoice, focus![0]); // mais frequente primeiro
    }

    [Fact]
    public async Task CreatePracticeAsync_without_focus_passes_no_categories()
    {
        var stub = new StubLlmProvider(SampleExercise());
        await Service(stub).CreatePracticeAsync(CreateRequest(focusOnWeaknesses: false));

        Assert.Null(stub.LastGenerationRequest!.FocusCategories);
    }

    [Fact]
    public async Task CreatePracticeAsync_with_focus_but_no_history_passes_no_categories()
    {
        // Foco pedido, mas sem histórico ainda → nada pra mirar.
        var stub = new StubLlmProvider(SampleExercise());
        await Service(stub).CreatePracticeAsync(CreateRequest(focusOnWeaknesses: true));

        Assert.Null(stub.LastGenerationRequest!.FocusCategories);
    }

    // ── Rascunho ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveDraftAsync_saves_translation_on_in_progress()
    {
        var stub = new StubLlmProvider(SampleExercise());
        var created = await Service(stub).CreatePracticeAsync(CreateRequest());

        var outcome = await Service(stub).SaveDraftAsync(created.Id, "rascunho parcial");
        Assert.Equal(PracticeOutcome.Ok, outcome);

        var reloaded = await Service(stub).GetPracticeAsync(created.Id);
        Assert.Equal("rascunho parcial", reloaded!.UserTranslation);
        Assert.Equal(PracticeStatus.InProgress, reloaded.Status);
    }

    [Fact]
    public async Task SaveDraftAsync_on_missing_returns_NotFound() =>
        Assert.Equal(PracticeOutcome.NotFound,
            await Service(new StubLlmProvider()).SaveDraftAsync(999, "x"));

    [Fact]
    public async Task SaveDraftAsync_on_completed_returns_ReadOnly()
    {
        var stub = new StubLlmProvider(SampleExercise(), CorrectionWith(ErrorCategory.Article));
        var created = await Service(stub).CreatePracticeAsync(CreateRequest());
        await Service(stub).CorrectPracticeAsync(created.Id, "x");

        Assert.Equal(PracticeOutcome.ReadOnly,
            await Service(stub).SaveDraftAsync(created.Id, "não pode mais"));
    }

    // ── Corrigir (concluir) ──────────────────────────────────────────────────

    [Fact]
    public async Task CorrectPracticeAsync_completes_same_attempt_and_persists_errors()
    {
        var stub = new StubLlmProvider(SampleExercise(), CorrectionWith(
            ErrorCategory.Article, ErrorCategory.WordChoice));
        var created = await Service(stub).CreatePracticeAsync(CreateRequest());
        Assert.Equal(PracticeStatus.InProgress, created.Status);

        var (outcome, detail) = await Service(stub).CorrectPracticeAsync(created.Id, "I have a car.");

        Assert.Equal(PracticeOutcome.Ok, outcome);
        Assert.Equal(PracticeStatus.Completed, detail!.Status);
        Assert.Equal("I have a car.", detail.UserTranslation);
        Assert.Equal("I have a car.", detail.CorrectedText);
        Assert.NotNull(detail.CompletedAt);
        Assert.Equal(2, detail.Errors.Count);

        // Atualizou a MESMA tentativa — não criou uma nova.
        await using var ctx = _db.NewContext();
        Assert.Equal(1, ctx.Exercises.Count());
        Assert.Equal(2, ctx.Errors.Count());
    }

    [Fact]
    public async Task CorrectPracticeAsync_is_readonly_after_completion()
    {
        var stub = new StubLlmProvider(SampleExercise(), CorrectionWith(ErrorCategory.Article));
        var created = await Service(stub).CreatePracticeAsync(CreateRequest());
        await Service(stub).CorrectPracticeAsync(created.Id, "primeira");

        var (outcome, detail) = await Service(stub).CorrectPracticeAsync(created.Id, "segunda");
        Assert.Equal(PracticeOutcome.ReadOnly, outcome);
        Assert.Null(detail);
    }

    [Fact]
    public async Task CorrectPracticeAsync_on_missing_returns_NotFound()
    {
        var (outcome, detail) = await Service(new StubLlmProvider()).CorrectPracticeAsync(999, "x");
        Assert.Equal(PracticeOutcome.NotFound, outcome);
        Assert.Null(detail);
    }

    // ── Ler / listar ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPracticeAsync_returns_null_for_missing() =>
        Assert.Null(await Service(new StubLlmProvider()).GetPracticeAsync(999));

    [Fact]
    public async Task ListPracticesAsync_orders_newest_first()
    {
        await using (var ctx = _db.NewContext())
        {
            ctx.Exercises.Add(new ExerciseAttempt
            {
                SourceText = "antiga",
                Status = PracticeStatus.Completed,
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
            });
            ctx.Exercises.Add(new ExerciseAttempt
            {
                SourceText = "recente",
                Status = PracticeStatus.InProgress,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var list = await Service(new StubLlmProvider()).ListPracticesAsync();

        Assert.Equal(2, list.Count);
        Assert.Equal("recente", list[0].Preview);
        Assert.Equal("antiga", list[1].Preview);
    }

    [Fact]
    public async Task ListPracticesAsync_summarizes_and_truncates_preview()
    {
        var longText = new string('a', 200);
        var stub = new StubLlmProvider(SampleExercise(longText));
        await Service(stub).CreatePracticeAsync(CreateRequest());

        var item = Assert.Single(await Service(stub).ListPracticesAsync());
        Assert.Equal(PracticeStatus.InProgress, item.Status);
        Assert.Equal(0, item.ErrorCount);
        Assert.Equal(120, item.Preview.Length); // truncado
    }

    // ── Excluir ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeletePracticeAsync_removes_practice_and_its_errors()
    {
        var stub = new StubLlmProvider(SampleExercise(), CorrectionWith(
            ErrorCategory.Article, ErrorCategory.Spelling));
        var created = await Service(stub).CreatePracticeAsync(CreateRequest());
        await Service(stub).CorrectPracticeAsync(created.Id, "x");

        Assert.Equal(PracticeOutcome.Ok, await Service(stub).DeletePracticeAsync(created.Id));

        await using var ctx = _db.NewContext();
        Assert.Equal(0, ctx.Exercises.Count());
        Assert.Equal(0, ctx.Errors.Count()); // cascade
    }

    [Fact]
    public async Task DeletePracticeAsync_on_missing_returns_NotFound() =>
        Assert.Equal(PracticeOutcome.NotFound,
            await Service(new StubLlmProvider()).DeletePracticeAsync(999));

    // ── Perfil ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetProfileAsync_on_empty_db_is_all_zero()
    {
        var profile = await Service(new StubLlmProvider()).GetProfileAsync();

        Assert.Equal(0, profile.TotalAttempts);
        Assert.Equal(0, profile.TotalErrors);
        Assert.Empty(profile.ByCategory);
    }

    [Fact]
    public async Task GetProfileAsync_counts_only_completed_practices()
    {
        // Uma em andamento (não deve entrar na conta).
        await Service(new StubLlmProvider(SampleExercise())).CreatePracticeAsync(CreateRequest());

        // Uma concluída, com 2 erros.
        var stub = new StubLlmProvider(SampleExercise(), CorrectionWith(
            ErrorCategory.WordChoice, ErrorCategory.Spelling));
        var created = await Service(stub).CreatePracticeAsync(CreateRequest());
        await Service(stub).CorrectPracticeAsync(created.Id, "x");

        var profile = await Service(new StubLlmProvider()).GetProfileAsync();
        Assert.Equal(1, profile.TotalAttempts); // só a concluída
        Assert.Equal(2, profile.TotalErrors);
        Assert.Equal(2, profile.ByCategory.Count);
    }

    [Fact]
    public async Task GetProfileAsync_aggregates_and_orders_by_frequency()
    {
        // Prática 1 concluída: WordChoice ×2, Spelling ×1
        var stub1 = new StubLlmProvider(SampleExercise(), CorrectionWith(
            ErrorCategory.WordChoice, ErrorCategory.WordChoice, ErrorCategory.Spelling));
        var c1 = await Service(stub1).CreatePracticeAsync(CreateRequest());
        await Service(stub1).CorrectPracticeAsync(c1.Id, "x");

        // Prática 2 concluída: WordChoice ×1, VerbTense ×2
        var stub2 = new StubLlmProvider(SampleExercise(), CorrectionWith(
            ErrorCategory.WordChoice, ErrorCategory.VerbTense, ErrorCategory.VerbTense));
        var c2 = await Service(stub2).CreatePracticeAsync(CreateRequest());
        await Service(stub2).CorrectPracticeAsync(c2.Id, "y");

        var profile = await Service(new StubLlmProvider()).GetProfileAsync();

        Assert.Equal(2, profile.TotalAttempts);
        Assert.Equal(6, profile.TotalErrors);

        // Ordenado da fraqueza mais frequente pra menos: WordChoice(3) > VerbTense(2) > Spelling(1).
        Assert.Collection(profile.ByCategory,
            c => { Assert.Equal(ErrorCategory.WordChoice, c.Category); Assert.Equal(3, c.Count); },
            c => { Assert.Equal(ErrorCategory.VerbTense, c.Category); Assert.Equal(2, c.Count); },
            c => { Assert.Equal(ErrorCategory.Spelling, c.Category); Assert.Equal(1, c.Count); });
    }

    public void Dispose() => _db.Dispose();
}
