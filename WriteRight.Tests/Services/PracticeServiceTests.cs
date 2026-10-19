using Microsoft.EntityFrameworkCore;
using WriteRight.Api.Data;
using WriteRight.Api.Llm;
using WriteRight.Api.Services;
using WriteRight.Shared;
using WriteRight.Shared.Corrections;
using WriteRight.Shared.Exercises;
using WriteRight.Shared.Practices;
using WriteRight.Shared.Profile;
using WriteRight.Shared.Taxonomy;
using WriteRight.Shared.Usage;
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

    /// <summary>Singleton em produção — um só por teste, pra dar pra ver se a correção o acordou.</summary>
    private readonly CardJobSignal _cardJobs = new();

    // Um serviço novo por operação = um contexto novo, como em produção (scoped por request).
    // O UsageService compartilha o MESMO contexto de propósito: ele só enfileira o
    // registro de consumo e quem salva é o PracticeService. Contextos separados
    // fariam o consumo nunca ser persistido — em produção o Scoped do DI garante isso.
    private PracticeService Service(StubLlmProvider stub)
    {
        var ctx = _db.NewContext();
        return new(stub, ctx, new UsageService(ctx, TestPricing.Default()), _cardJobs);
    }

    private static CreatePracticeRequest CreateRequest(bool focusOnWeaknesses = false) =>
        new(Language.Portuguese, Language.English, 60, CefrLevel.B1, Theme: "cotidiano", FocusOnWeaknesses: focusOnWeaknesses);

    private static GeneratedExercise SampleExercise(string? sourceText = null) =>
        new(sourceText ?? "Um texto gerado.");

    private static CorrectionResult CorrectionWith(params ErrorCategory[] categories)
    {
        var errors = categories
            .Select(c => new WritingError(c, ErrorSeverity.Understandable, "x", "y", "porquê"))
            .ToList();
        return new CorrectionResult("I have a car.", errors);
    }

    private static WritingError Err(ErrorCategory category, ErrorSeverity severity) =>
        new(category, severity, "x", "y", "porquê");

    /// <summary>
    /// Semeia uma prática CONCLUÍDA direto no banco, com <paramref name="completedAt"/>
    /// e erros explícitos. Dá controle total sobre severidade e sobre a ordem temporal
    /// (o <c>CorrectPracticeAsync</c> carimbaria tudo com UtcNow) — o que o recorte
    /// recente e a ponderação por gravidade precisam pra ser testados de forma determinística.
    /// </summary>
    private Task<int> SeedCompletedAsync(DateTimeOffset completedAt, params WritingError[] errors) =>
        SeedCompletedAsync(completedAt, "seed", errors);

    /// <summary>Idem, com a tradução escolhida — a linha do tempo conta as palavras dela.</summary>
    private async Task<int> SeedCompletedAsync(DateTimeOffset completedAt, string translation, params WritingError[] errors)
    {
        await using var ctx = _db.NewContext();
        var practice = new ExerciseAttempt
        {
            SourceLanguage = Language.Portuguese,
            TargetLanguage = Language.English,
            Level = CefrLevel.B1,
            Status = PracticeStatus.Completed,
            SourceText = "seed",
            UserTranslation = translation,
            CreatedAt = completedAt,
            CompletedAt = completedAt,
            Errors = errors.Select(e => new ExerciseError
            {
                Category = e.Category,
                Severity = e.Severity,
                Original = e.Original,
                Correction = e.Correction,
                Explanation = e.Explanation,
            }).ToList(),
        };
        ctx.Exercises.Add(practice);
        await ctx.SaveChangesAsync();
        return practice.Id;
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
    public async Task CreatePracticeAsync_always_rolls_the_text_variety()
    {
        // Sem foco, o resto do prompt é idêntico em toda geração — a variedade
        // sorteada é a única coisa que impede o modelo de devolver sempre o mesmo texto.
        var stub = new StubLlmProvider(SampleExercise());
        await Service(stub).CreatePracticeAsync(CreateRequest(focusOnWeaknesses: false));

        var variety = stub.LastGenerationRequest!.Variety;
        Assert.NotNull(variety);
        Assert.Contains(variety!.Domain, VarietyCatalog.Domains);
        Assert.Contains(variety.Tense, VarietyCatalog.Tenses);
    }

    [Fact]
    public async Task CreatePracticeAsync_with_focus_but_no_history_passes_no_categories()
    {
        // Foco pedido, mas sem histórico ainda → nada pra mirar.
        var stub = new StubLlmProvider(SampleExercise());
        await Service(stub).CreatePracticeAsync(CreateRequest(focusOnWeaknesses: true));

        Assert.Null(stub.LastGenerationRequest!.FocusCategories);
    }

    [Fact]
    public async Task CreatePracticeAsync_rejects_same_source_and_target()
    {
        // Não faz sentido traduzir de um idioma pra ele mesmo.
        var request = new CreatePracticeRequest(
            Language.English, Language.English, 60, CefrLevel.B1);

        await Assert.ThrowsAsync<ArgumentException>(
            () => Service(new StubLlmProvider(SampleExercise())).CreatePracticeAsync(request));
    }

    /// <summary>
    /// O min/max do campo não impede digitar 2000. A recusa vem ANTES da chamada:
    /// muito acima do teto, a geração estoura o limite de saída e volta cortada — paga
    /// e inútil. Nem chamada, nem gasto registrado.
    /// </summary>
    [Theory]
    [InlineData(PracticeLimits.MinWords - 1)]
    [InlineData(PracticeLimits.MaxWords + 1)]
    [InlineData(2000)]
    public async Task CreatePracticeAsync_rejects_a_word_count_out_of_range_before_the_call(int words)
    {
        var stub = new StubLlmProvider(SampleExercise());
        var request = new CreatePracticeRequest(Language.Portuguese, Language.English, words, CefrLevel.B1);

        await Assert.ThrowsAsync<ArgumentException>(() => Service(stub).CreatePracticeAsync(request));

        Assert.Null(stub.LastGenerationRequest);
        await using var ctx = _db.NewContext();
        Assert.Empty(ctx.Exercises);
        Assert.Empty(ctx.LlmCalls);
    }

    [Theory]
    [InlineData(PracticeLimits.MinWords)]
    [InlineData(PracticeLimits.MaxWords)]
    public async Task CreatePracticeAsync_accepts_the_word_count_limits_themselves(int words)
    {
        var request = new CreatePracticeRequest(Language.Portuguese, Language.English, words, CefrLevel.B1);

        var detail = await Service(new StubLlmProvider(SampleExercise())).CreatePracticeAsync(request);

        Assert.Equal(PracticeStatus.InProgress, detail.Status);
    }

    [Fact]
    public async Task Database_rejects_practice_with_same_languages()
    {
        // Blindagem de última instância: mesmo forçando por fora do serviço, o
        // CHECK constraint do banco barra origem == alvo.
        await using var ctx = _db.NewContext();
        ctx.Exercises.Add(new ExerciseAttempt
        {
            SourceLanguage = Language.English,
            TargetLanguage = Language.English,
            Status = PracticeStatus.InProgress,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
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

    /// <summary>
    /// A correção NÃO cunha card nem chama o desenho de cards: só deixa a promessa —
    /// a prática pendente, gravada na mesma transação — e acorda o worker. Se a
    /// promessa não entrasse junto, um restart entre a correção e o worker perderia
    /// os cards daquela prática em silêncio.
    /// </summary>
    [Fact]
    public async Task Correcting_queues_the_cards_instead_of_minting_them()
    {
        var stub = new StubLlmProvider(SampleExercise(), CorrectionWith(ErrorCategory.WordChoice));
        var created = await Service(stub).CreatePracticeAsync(CreateRequest());

        await Service(stub).CorrectPracticeAsync(created.Id, "I have a car.");

        await using var ctx = _db.NewContext();
        Assert.Equal(CardsStatus.Pending, (await ctx.Exercises.SingleAsync()).CardsStatus);
        Assert.Empty(ctx.Cards);
        Assert.Equal(0, stub.CardCalls);
        Assert.True(_cardJobs.WaitAsync(CancellationToken.None).IsCompleted, "a correção não acordou o worker");
    }

    /// <summary>Prática em andamento não tem cards a fazer — "não se aplica", não "pendente".</summary>
    [Fact]
    public async Task A_practice_in_progress_has_no_card_status()
    {
        var stub = new StubLlmProvider(SampleExercise());
        await Service(stub).CreatePracticeAsync(CreateRequest());

        await using var ctx = _db.NewContext();
        Assert.Null((await ctx.Exercises.SingleAsync()).CardsStatus);
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

    /// <summary>
    /// Corrigir nada ainda seria uma chamada cobrada. A recusa vem antes dela, e a
    /// prática continua em andamento — dá pra escrever e corrigir depois.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public async Task CorrectPracticeAsync_rejects_a_blank_translation_before_the_call(string blank)
    {
        var stub = new StubLlmProvider(SampleExercise(), CorrectionWith(ErrorCategory.Article));
        var created = await Service(stub).CreatePracticeAsync(CreateRequest());

        await Assert.ThrowsAsync<ArgumentException>(() => Service(stub).CorrectPracticeAsync(created.Id, blank));

        Assert.Null(stub.LastCorrectionRequest);
        var reloaded = await Service(stub).GetPracticeAsync(created.Id);
        Assert.Equal(PracticeStatus.InProgress, reloaded!.Status);
        Assert.Empty(reloaded.Errors);
    }

    // ── Ler / listar ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPracticeAsync_returns_null_for_missing() =>
        Assert.Null(await Service(new StubLlmProvider()).GetPracticeAsync(999));

    /// <summary>
    /// Semeia práticas na ordem dada, uma por save — é o que garante que o Id cresça
    /// na mesma ordem, como em produção (cada prática nasce no seu próprio insert).
    /// </summary>
    private async Task SeedListAsync(params (string Text, PracticeStatus Status)[] practices)
    {
        await using var ctx = _db.NewContext();
        foreach (var (text, status) in practices)
        {
            ctx.Exercises.Add(new ExerciseAttempt
            {
                SourceLanguage = Language.Portuguese,
                TargetLanguage = Language.English,
                SourceText = text,
                Status = status,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }
    }

    private async Task<string[]> ListPreviewsAsync(int skip = 0, int take = PracticeService.PageSize) =>
        (await Service(new StubLlmProvider()).ListPracticesAsync(skip, take)).Items
            .Select(p => p.Preview).ToArray();

    [Fact]
    public async Task ListPracticesAsync_orders_newest_first()
    {
        await SeedListAsync(("antiga", PracticeStatus.Completed), ("recente", PracticeStatus.Completed));

        Assert.Equal(["recente", "antiga"], await ListPreviewsAsync());
    }

    [Fact]
    public async Task ListPracticesAsync_puts_in_progress_first_even_when_older()
    {
        // A em andamento é a única que pede ação: com paginação, deixá-la no lugar
        // cronológico a enterraria embaixo de páginas de práticas concluídas.
        await SeedListAsync(
            ("andamento antiga", PracticeStatus.InProgress),
            ("concluída", PracticeStatus.Completed),
            ("andamento nova", PracticeStatus.InProgress));

        Assert.Equal(["andamento nova", "andamento antiga", "concluída"], await ListPreviewsAsync());
    }

    [Fact]
    public async Task ListPracticesAsync_pages_with_skip_and_reports_the_whole_total()
    {
        await SeedListAsync(
            ("p1", PracticeStatus.Completed), ("p2", PracticeStatus.Completed),
            ("p3", PracticeStatus.Completed), ("p4", PracticeStatus.Completed),
            ("p5", PracticeStatus.Completed));

        Assert.Equal(["p5", "p4"], await ListPreviewsAsync(skip: 0, take: 2));
        Assert.Equal(["p3", "p2"], await ListPreviewsAsync(skip: 2, take: 2));
        Assert.Equal(["p1"], await ListPreviewsAsync(skip: 4, take: 2));
        Assert.Empty(await ListPreviewsAsync(skip: 5, take: 2));

        // O total é da lista inteira, não da página — é por ele que o cliente sabe se há mais.
        var page = await Service(new StubLlmProvider()).ListPracticesAsync(skip: 4, take: 2);
        Assert.Equal(5, page.Total);
    }

    [Fact]
    public async Task ListPracticesAsync_summarizes_and_truncates_preview()
    {
        var longText = new string('a', 200);
        var stub = new StubLlmProvider(SampleExercise(longText));
        await Service(stub).CreatePracticeAsync(CreateRequest());

        var item = Assert.Single((await Service(stub).ListPracticesAsync()).Items);
        Assert.Equal(PracticeStatus.InProgress, item.Status);
        Assert.Equal(0, item.ErrorCount);
        Assert.Equal(new string('a', 160) + "…", item.Preview); // cortado, e diz que foi
    }

    [Fact]
    public async Task ListCompletionTimesAsync_returns_only_completed_practices()
    {
        var stub = new StubLlmProvider(SampleExercise(), CorrectionWith(ErrorCategory.Article));
        var completed = await Service(stub).CreatePracticeAsync(CreateRequest());
        var corrected = (await Service(stub).CorrectPracticeAsync(completed.Id, "x")).Detail!;
        await Service(stub).CreatePracticeAsync(CreateRequest()); // fica em andamento

        var times = await Service(stub).ListCompletionTimesAsync();

        Assert.Equal(corrected.CompletedAt, Assert.Single(times));
    }

    // ── Excluir ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeletePracticeAsync_hides_the_practice_but_keeps_the_row_and_its_errors()
    {
        var stub = new StubLlmProvider(SampleExercise(), CorrectionWith(
            ErrorCategory.Article, ErrorCategory.Spelling));
        var created = await Service(stub).CreatePracticeAsync(CreateRequest());
        await Service(stub).CorrectPracticeAsync(created.Id, "x");

        Assert.Equal(PracticeOutcome.Ok, await Service(stub).DeletePracticeAsync(created.Id));

        // Some de tudo que a tela lê...
        Assert.Null(await Service(stub).GetPracticeAsync(created.Id));
        var page = await Service(stub).ListPracticesAsync();
        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);

        // ...mas não do banco: exclusão lógica.
        await using var ctx = _db.NewContext();
        var row = Assert.Single(ctx.Exercises.IgnoreQueryFilters());
        Assert.NotNull(row.DeletedAt);
        Assert.Equal(2, ctx.Errors.IgnoreQueryFilters().Count());
    }

    /// <summary>
    /// Excluiu porque o texto não servia: os erros dele não podem continuar pesando
    /// no perfil — que é o que direciona a próxima geração.
    /// </summary>
    [Fact]
    public async Task A_deleted_practice_leaves_the_profile_and_the_category_review()
    {
        var stub = new StubLlmProvider(SampleExercise(), CorrectionWith(ErrorCategory.Article));
        var kept = await Service(stub).CreatePracticeAsync(CreateRequest());
        await Service(stub).CorrectPracticeAsync(kept.Id, "x");
        var deleted = await Service(stub).CreatePracticeAsync(CreateRequest());
        await Service(stub).CorrectPracticeAsync(deleted.Id, "x");

        await Service(stub).DeletePracticeAsync(deleted.Id);

        var profile = await Service(stub).GetProfileAsync();
        Assert.Equal(1, profile.Lifetime.Attempts);
        Assert.Equal(1, profile.Lifetime.TotalErrors);
        Assert.Equal(kept.Id, Assert.Single(profile.Timeline).PracticeId); // nem coluna no gráfico
        var review = await Service(stub).GetCategoryErrorsAsync(ErrorCategory.Article);
        Assert.Equal(kept.Id, Assert.Single(review).PracticeId);
    }

    [Fact]
    public async Task DeletePracticeAsync_on_missing_returns_NotFound() =>
        Assert.Equal(PracticeOutcome.NotFound,
            await Service(new StubLlmProvider()).DeletePracticeAsync(999));

    [Fact]
    public async Task Deleting_twice_returns_NotFound()
    {
        var stub = new StubLlmProvider(SampleExercise());
        var created = await Service(stub).CreatePracticeAsync(CreateRequest());
        await Service(stub).DeletePracticeAsync(created.Id);

        Assert.Equal(PracticeOutcome.NotFound, await Service(stub).DeletePracticeAsync(created.Id));
    }

    /// <summary>
    /// Excluir é arrumar a lista, não desfazer o dia em que você praticou: apagar a
    /// única prática de cinco dias atrás não pode quebrar a série.
    /// </summary>
    [Fact]
    public async Task ListCompletionTimesAsync_still_counts_a_deleted_practice()
    {
        var stub = new StubLlmProvider(SampleExercise(), CorrectionWith(ErrorCategory.Article));
        var created = await Service(stub).CreatePracticeAsync(CreateRequest());
        var corrected = (await Service(stub).CorrectPracticeAsync(created.Id, "x")).Detail!;

        await Service(stub).DeletePracticeAsync(created.Id);

        Assert.Equal(corrected.CompletedAt, Assert.Single(await Service(stub).ListCompletionTimesAsync()));
    }

    /// <summary>
    /// A regra "nada se apaga de verdade" é do schema, não da disciplina de quem
    /// escreve o serviço: um DELETE físico numa prática com erros é recusado.
    /// </summary>
    [Fact]
    public async Task A_physical_delete_of_a_practice_is_refused_by_the_schema()
    {
        var stub = new StubLlmProvider(SampleExercise(), CorrectionWith(ErrorCategory.Article));
        var created = await Service(stub).CreatePracticeAsync(CreateRequest());
        await Service(stub).CorrectPracticeAsync(created.Id, "x");

        await using var ctx = _db.NewContext();
        ctx.Exercises.Remove(await ctx.Exercises.SingleAsync());

        await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
    }

    // ── Perfil ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetProfileAsync_on_empty_db_is_all_zero()
    {
        var profile = await Service(new StubLlmProvider()).GetProfileAsync();

        Assert.Equal(0, profile.Lifetime.Attempts);
        Assert.Equal(0, profile.Lifetime.TotalErrors);
        Assert.Empty(profile.Lifetime.ByCategory);
        Assert.Equal(0, profile.Recent.Attempts);
        Assert.Empty(profile.Recent.ByCategory);
        Assert.Empty(profile.Timeline);
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
        Assert.Equal(1, profile.Lifetime.Attempts); // só a concluída
        Assert.Equal(2, profile.Lifetime.TotalErrors);
        Assert.Equal(2, profile.Lifetime.ByCategory.Count);
    }

    [Fact]
    public async Task GetProfileAsync_aggregates_and_orders_by_weighted_score()
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

        Assert.Equal(2, profile.Lifetime.Attempts);
        Assert.Equal(6, profile.Lifetime.TotalErrors);

        // Tudo "Compreensível" (peso 2) → o Score é 2× a contagem; a ordem segue a
        // frequência: WordChoice(3, peso 6) > VerbTense(2, peso 4) > Spelling(1, peso 2).
        Assert.Collection(profile.Lifetime.ByCategory,
            c => { Assert.Equal(ErrorCategory.WordChoice, c.Category); Assert.Equal(3, c.Count); Assert.Equal(6, c.Score); },
            c => { Assert.Equal(ErrorCategory.VerbTense, c.Category); Assert.Equal(2, c.Count); Assert.Equal(4, c.Score); },
            c => { Assert.Equal(ErrorCategory.Spelling, c.Category); Assert.Equal(1, c.Count); Assert.Equal(2, c.Score); });
    }

    [Fact]
    public async Task GetProfileAsync_ranks_by_severity_weight_over_raw_frequency()
    {
        // Spelling ×2, mas só "Lapidação" (peso 1 cada = 2). Preposition ×1, mas
        // "Quebra o sentido" (peso 3). O que atrapalha mais deve liderar, apesar de raro.
        await SeedCompletedAsync(DateTimeOffset.UtcNow,
            Err(ErrorCategory.Spelling, ErrorSeverity.Polish),
            Err(ErrorCategory.Spelling, ErrorSeverity.Polish),
            Err(ErrorCategory.Preposition, ErrorSeverity.BreaksMeaning));

        var profile = await Service(new StubLlmProvider()).GetProfileAsync();

        Assert.Collection(profile.Lifetime.ByCategory,
            c => { Assert.Equal(ErrorCategory.Preposition, c.Category); Assert.Equal(1, c.Count); Assert.Equal(3, c.Score); },
            c => { Assert.Equal(ErrorCategory.Spelling, c.Category); Assert.Equal(2, c.Count); Assert.Equal(2, c.Score); });
        Assert.Equal(3, profile.Lifetime.TotalErrors);
    }

    [Fact]
    public async Task GetProfileAsync_recent_window_keeps_only_the_newest_completed()
    {
        // Uma prática a mais que a janela, categoria distinta em cada, da mais nova pra mais antiga.
        var cats = Enum.GetValues<ErrorCategory>().Take(PracticeService.RecentWindow + 1).ToArray();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < cats.Length; i++)
            await SeedCompletedAsync(now.AddMinutes(-i), Err(cats[i], ErrorSeverity.Understandable));
        var oldest = cats[^1]; // a mais antiga → deve cair fora do recorte recente

        var profile = await Service(new StubLlmProvider()).GetProfileAsync();

        Assert.Equal(cats.Length, profile.Lifetime.Attempts);
        Assert.Equal(cats.Length, profile.Lifetime.ByCategory.Count);

        Assert.Equal(PracticeService.RecentWindow, profile.Recent.Attempts);
        Assert.Equal(PracticeService.RecentWindow, profile.Recent.ByCategory.Count);
        Assert.DoesNotContain(profile.Recent.ByCategory, c => c.Category == oldest);
        Assert.Contains(profile.Lifetime.ByCategory, c => c.Category == oldest);
    }

    [Fact]
    public async Task GetProfileAsync_recent_can_be_clean_while_lifetime_has_errors()
    {
        var now = DateTimeOffset.UtcNow;
        // Uma antiga com erro; a janela recente inteira impecável (sem erros).
        await SeedCompletedAsync(now.AddMinutes(-100), Err(ErrorCategory.Article, ErrorSeverity.Understandable));
        for (var i = 0; i < PracticeService.RecentWindow; i++)
            await SeedCompletedAsync(now.AddMinutes(-i));

        var profile = await Service(new StubLlmProvider()).GetProfileAsync();

        Assert.Equal(PracticeService.RecentWindow + 1, profile.Lifetime.Attempts);
        Assert.Single(profile.Lifetime.ByCategory); // Article, do histórico
        Assert.Equal(PracticeService.RecentWindow, profile.Recent.Attempts);
        Assert.Empty(profile.Recent.ByCategory); // limpo na janela recente
    }

    /// <summary>
    /// O insumo do gráfico de evolução: uma prática por ponto, da mais antiga pra mais
    /// nova (não na ordem de gravação), com as palavras da tradução e os erros por
    /// gravidade. A em andamento fica de fora.
    /// </summary>
    [Fact]
    public async Task GetProfileAsync_timeline_has_one_point_per_completed_practice_oldest_first()
    {
        var now = DateTimeOffset.UtcNow;
        var newer = await SeedCompletedAsync(now, "I went  to the\nbeach",
            Err(ErrorCategory.Spelling, ErrorSeverity.Polish));
        var older = await SeedCompletedAsync(now.AddDays(-1), "It was sunny",
            Err(ErrorCategory.Article, ErrorSeverity.BreaksMeaning),
            Err(ErrorCategory.Preposition, ErrorSeverity.Understandable),
            Err(ErrorCategory.Article, ErrorSeverity.Understandable));
        var clean = await SeedCompletedAsync(now.AddDays(-2), "Hello");
        await Service(new StubLlmProvider(SampleExercise())).CreatePracticeAsync(CreateRequest());

        var profile = await Service(new StubLlmProvider()).GetProfileAsync();

        Assert.Collection(profile.Timeline,
            p =>
            {
                Assert.Equal(clean, p.PracticeId);
                Assert.Equal(1, p.Words);
                Assert.Equal(new SeverityCounts(0, 0, 0), p.Errors);
            },
            p =>
            {
                Assert.Equal(older, p.PracticeId);
                Assert.Equal(CefrLevel.B1, p.Level);
                Assert.Equal(3, p.Words);
                Assert.Equal(new SeverityCounts(1, 2, 0), p.Errors);
            },
            p =>
            {
                Assert.Equal(newer, p.PracticeId);
                Assert.Equal(5, p.Words);
                Assert.Equal(new SeverityCounts(0, 0, 1), p.Errors);
            });
    }

    // ── Revisão: erros reais por categoria ───────────────────────────────────

    [Fact]
    public async Task GetCategoryErrorsAsync_returns_only_that_category_newest_first()
    {
        var older = DateTimeOffset.UtcNow.AddHours(-1);
        var newer = DateTimeOffset.UtcNow;
        await SeedCompletedAsync(older,
            new WritingError(ErrorCategory.Preposition, ErrorSeverity.BreaksMeaning, "in home", "at home", "usa 'at'"),
            new WritingError(ErrorCategory.Spelling, ErrorSeverity.Polish, "adress", "address", "dois 'd'"));
        await SeedCompletedAsync(newer,
            new WritingError(ErrorCategory.Preposition, ErrorSeverity.Understandable, "to work", "for work", "é 'for work'"));

        var items = await Service(new StubLlmProvider()).GetCategoryErrorsAsync(ErrorCategory.Preposition);

        Assert.Equal(2, items.Count);               // só as preposições, não o Spelling
        Assert.Equal("to work", items[0].Original); // mais recente primeiro
        Assert.Equal("in home", items[1].Original);
        Assert.Equal(ErrorSeverity.BreaksMeaning, items[1].Severity);
        Assert.DoesNotContain(items, i => i.Original == "adress");
        Assert.All(items, i => Assert.True(i.PracticeId > 0)); // traz a prática de origem
    }

    [Fact]
    public async Task GetCategoryErrorsAsync_is_empty_for_category_without_errors()
    {
        await SeedCompletedAsync(DateTimeOffset.UtcNow, Err(ErrorCategory.Spelling, ErrorSeverity.Polish));

        var items = await Service(new StubLlmProvider()).GetCategoryErrorsAsync(ErrorCategory.Preposition);

        Assert.Empty(items);
    }

    [Fact]
    public async Task GetCategoryErrorsAsync_ignores_errors_from_unfinished_practices()
    {
        // Uma concluída com o erro (deve vir); e uma "em andamento" com um erro plantado
        // à força (não ocorre pela aplicação, mas o filtro por concluída tem que barrar).
        await SeedCompletedAsync(DateTimeOffset.UtcNow,
            Err(ErrorCategory.Preposition, ErrorSeverity.Understandable));
        await using (var ctx = _db.NewContext())
        {
            ctx.Exercises.Add(new ExerciseAttempt
            {
                SourceLanguage = Language.Portuguese,
                TargetLanguage = Language.English,
                Status = PracticeStatus.InProgress,
                SourceText = "x",
                Errors =
                {
                    new ExerciseError
                    {
                        Category = ErrorCategory.Preposition,
                        Severity = ErrorSeverity.Understandable,
                        Original = "planted",
                        Correction = "y",
                        Explanation = "z",
                    },
                },
            });
            await ctx.SaveChangesAsync();
        }

        var items = await Service(new StubLlmProvider()).GetCategoryErrorsAsync(ErrorCategory.Preposition);

        Assert.Single(items);
        Assert.DoesNotContain(items, i => i.Original == "planted");
    }

    // ── Consumo ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreatePracticeAsync_records_generation_usage_linked_to_the_practice()
    {
        var detail = await Service(new StubLlmProvider(SampleExercise())).CreatePracticeAsync(CreateRequest());

        await using var ctx = _db.NewContext();
        var call = Assert.Single(ctx.LlmCalls);
        Assert.Equal(LlmOperation.Generation, call.Operation);
        Assert.Equal(detail.Id, call.PracticeId);
        Assert.Null(call.AnalysisId);
        Assert.Equal(StubLlmProvider.DefaultUsage.InputTokens, call.InputTokens);
        Assert.Equal(StubLlmProvider.DefaultUsage.OutputTokens, call.OutputTokens);
        Assert.True(call.CostUsd > 0);
        Assert.Equal(12_345, call.DurationMs);
    }

    [Fact]
    public async Task CorrectPracticeAsync_records_a_second_call_on_the_same_practice()
    {
        // A prática faz chamadas em momentos diferentes (aqui, as duas que passam pelo
        // PracticeService). É o motivo de o consumo ser tabela própria em vez de colunas.
        var stub = new StubLlmProvider(SampleExercise(), CorrectionWith(ErrorCategory.Article));
        var detail = await Service(stub).CreatePracticeAsync(CreateRequest());
        await Service(stub).CorrectPracticeAsync(detail.Id, "my translation");

        await using var ctx = _db.NewContext();
        var calls = ctx.LlmCalls.Where(c => c.PracticeId == detail.Id).OrderBy(c => c.Id).ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal(LlmOperation.Generation, calls[0].Operation);
        Assert.Equal(LlmOperation.Correction, calls[1].Operation);
    }

    [Fact]
    public async Task DeletePracticeAsync_keeps_the_usage_record()
    {
        // O gasto aconteceu e é irreversível. Excluir a prática não pode apagar o
        // registro de que você pagou por ela — senão a fatura e o histórico de custo
        // divergem. E o registro continua apontando pra ela (FK), não pro vazio.
        var stub = new StubLlmProvider(SampleExercise(), CorrectionWith(ErrorCategory.Article));
        var detail = await Service(stub).CreatePracticeAsync(CreateRequest());
        await Service(stub).CorrectPracticeAsync(detail.Id, "my translation");

        var outcome = await Service(stub).DeletePracticeAsync(detail.Id);

        Assert.Equal(PracticeOutcome.Ok, outcome);
        await using var ctx = _db.NewContext();
        Assert.Equal(2, ctx.LlmCalls.Count(c => c.PracticeId == detail.Id)); // o custo sobrevive à exclusão
    }

    [Fact]
    public async Task CorrectPracticeAsync_persists_even_when_the_client_gives_up_mid_call()
    {
        // Bug real: o navegador estourou o timeout de 100s, o RequestAborted foi
        // cancelado, e o SaveChanges que usava esse token descartou consumo E correção
        // — com a chamada já paga. Gravação posterior à cobrança não é cancelável.
        var stub = new StubLlmProvider(SampleExercise(), CorrectionWith(ErrorCategory.Article));
        var detail = await Service(stub).CreatePracticeAsync(CreateRequest());

        var browserGaveUp = new CancellationTokenSource();
        stub.CancelDuringCall = browserGaveUp;

        var (outcome, _) = await Service(stub).CorrectPracticeAsync(
            detail.Id, "my translation", browserGaveUp.Token);

        Assert.Equal(PracticeOutcome.Ok, outcome);

        await using var ctx = _db.NewContext();
        // A correção sobreviveu: o usuário a encontra ao recarregar, sem pagar de novo.
        Assert.Equal(PracticeStatus.Completed, ctx.Exercises.Single().Status);
        Assert.Single(ctx.Errors);
        // E o gasto das duas chamadas está registrado.
        Assert.Equal(2, ctx.LlmCalls.Count());
    }

    [Fact]
    public async Task CreatePracticeAsync_records_usage_when_the_call_is_billed_but_fails()
    {
        // A API respondeu e cobrou, mas a resposta não virou texto (recusa, JSON
        // truncado). Não nasce prática nenhuma — o gasto tem que ficar mesmo assim.
        var stub = new StubLlmProvider(SampleExercise()) { FailAfterBilling = true };

        await Assert.ThrowsAsync<LlmCallFailedException>(
            () => Service(stub).CreatePracticeAsync(CreateRequest()));

        await using var ctx = _db.NewContext();
        Assert.Equal(0, ctx.Exercises.Count()); // nada de prática pela metade
        var call = Assert.Single(ctx.LlmCalls);
        Assert.Equal(LlmOperation.Generation, call.Operation);
        Assert.Null(call.PracticeId); // não há prática pra vincular
        Assert.True(call.CostUsd > 0);
    }

    [Fact]
    public async Task CorrectPracticeAsync_records_usage_when_the_call_is_billed_but_fails()
    {
        // O caso mais caro do app: a correção estoura o teto de saída, você paga pelos
        // tokens todos e o JSON não desserializa. A prática segue em andamento (dá pra
        // tentar de novo), mas o gasto não pode sumir.
        var stub = new StubLlmProvider(SampleExercise(), CorrectionWith(ErrorCategory.Article));
        var detail = await Service(stub).CreatePracticeAsync(CreateRequest());

        stub.FailAfterBilling = true;
        await Assert.ThrowsAsync<LlmCallFailedException>(
            () => Service(stub).CorrectPracticeAsync(detail.Id, "my translation"));

        await using var ctx = _db.NewContext();
        var practice = ctx.Exercises.Single();
        Assert.Equal(PracticeStatus.InProgress, practice.Status); // não concluiu
        Assert.Empty(ctx.Errors);

        var failed = ctx.LlmCalls.Single(c => c.Operation == LlmOperation.Correction);
        Assert.Equal(detail.Id, failed.PracticeId);
        Assert.True(failed.CostUsd > 0);
        Assert.Equal(12_345, failed.DurationMs); // a espera também aconteceu
    }

    public void Dispose() => _db.Dispose();
}
