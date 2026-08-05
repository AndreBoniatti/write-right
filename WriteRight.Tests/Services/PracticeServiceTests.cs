using Microsoft.EntityFrameworkCore;
using WriteRight.Api.Data;
using WriteRight.Api.Llm;
using WriteRight.Api.Services;
using WriteRight.Shared;
using WriteRight.Shared.Corrections;
using WriteRight.Shared.Exercises;
using WriteRight.Shared.Practices;
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

    // Um serviço novo por operação = um contexto novo, como em produção (scoped por request).
    // O UsageService compartilha o MESMO contexto de propósito: ele só enfileira o
    // registro de consumo e quem salva é o PracticeService. Contextos separados
    // fariam o consumo nunca ser persistido — em produção o Scoped do DI garante isso.
    private PracticeService Service(StubLlmProvider stub)
    {
        var ctx = _db.NewContext();
        return new(stub, ctx, new UsageService(ctx, TestPricing.Default()));
    }

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

    private static WritingError Err(ErrorCategory category, ErrorSeverity severity) =>
        new(category, severity, "x", "y", "porquê");

    /// <summary>
    /// Semeia uma prática CONCLUÍDA direto no banco, com <paramref name="completedAt"/>
    /// e erros explícitos. Dá controle total sobre severidade e sobre a ordem temporal
    /// (o <c>CorrectPracticeAsync</c> carimbaria tudo com UtcNow) — o que o recorte
    /// recente e a ponderação por gravidade precisam pra ser testados de forma determinística.
    /// </summary>
    private async Task SeedCompletedAsync(DateTimeOffset completedAt, params WritingError[] errors)
    {
        await using var ctx = _db.NewContext();
        ctx.Exercises.Add(new ExerciseAttempt
        {
            SourceLanguage = Language.Portuguese,
            TargetLanguage = Language.English,
            Level = CefrLevel.B1,
            Status = PracticeStatus.Completed,
            SourceText = "seed",
            UserTranslation = "seed",
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
        });
        await ctx.SaveChangesAsync();
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
                SourceLanguage = Language.Portuguese,
                TargetLanguage = Language.English,
                SourceText = "antiga",
                Status = PracticeStatus.Completed,
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
            });
            ctx.Exercises.Add(new ExerciseAttempt
            {
                SourceLanguage = Language.Portuguese,
                TargetLanguage = Language.English,
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

        Assert.Equal(0, profile.Lifetime.Attempts);
        Assert.Equal(0, profile.Lifetime.TotalErrors);
        Assert.Equal(0, profile.Lifetime.TotalScore);
        Assert.Empty(profile.Lifetime.ByCategory);
        Assert.Equal(0, profile.Recent.Attempts);
        Assert.Empty(profile.Recent.ByCategory);
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
        Assert.Equal(12, profile.Lifetime.TotalScore);
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
        Assert.Equal(5, profile.Lifetime.TotalScore);
    }

    [Fact]
    public async Task GetProfileAsync_recent_window_keeps_only_last_five_completed()
    {
        // 6 práticas concluídas, categoria distinta em cada, da mais nova pra mais antiga.
        var cats = new[]
        {
            ErrorCategory.Article, ErrorCategory.Preposition, ErrorCategory.Spelling,
            ErrorCategory.VerbTense, ErrorCategory.WordChoice, ErrorCategory.Pronoun,
        };
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < cats.Length; i++)
            await SeedCompletedAsync(now.AddMinutes(-i), Err(cats[i], ErrorSeverity.Understandable));
        // i=5 (Pronoun) é a mais antiga (-5min) → deve cair fora do recorte recente.

        var profile = await Service(new StubLlmProvider()).GetProfileAsync();

        Assert.Equal(6, profile.Lifetime.Attempts);
        Assert.Equal(6, profile.Lifetime.ByCategory.Count);

        Assert.Equal(5, profile.Recent.Attempts);
        Assert.Equal(5, profile.Recent.ByCategory.Count);
        Assert.DoesNotContain(profile.Recent.ByCategory, c => c.Category == ErrorCategory.Pronoun);
        Assert.Contains(profile.Lifetime.ByCategory, c => c.Category == ErrorCategory.Pronoun);
    }

    [Fact]
    public async Task GetProfileAsync_recent_can_be_clean_while_lifetime_has_errors()
    {
        var now = DateTimeOffset.UtcNow;
        // Uma antiga com erro; cinco recentes impecáveis (sem erros).
        await SeedCompletedAsync(now.AddMinutes(-10), Err(ErrorCategory.Article, ErrorSeverity.Understandable));
        for (var i = 0; i < 5; i++)
            await SeedCompletedAsync(now.AddMinutes(-i));

        var profile = await Service(new StubLlmProvider()).GetProfileAsync();

        Assert.Equal(6, profile.Lifetime.Attempts);
        Assert.Single(profile.Lifetime.ByCategory); // Article, do histórico
        Assert.Equal(5, profile.Recent.Attempts);
        Assert.Empty(profile.Recent.ByCategory); // limpo nas últimas cinco
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
    }

    [Fact]
    public async Task CorrectPracticeAsync_records_a_second_call_on_the_same_practice()
    {
        // Uma prática = DUAS chamadas em momentos diferentes. É o motivo de o consumo
        // ser tabela própria em vez de colunas na prática.
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
        // Referência fraca de propósito: o gasto aconteceu e é irreversível.
        // Excluir a prática não pode apagar o registro de que você pagou por ela —
        // senão a fatura e o histórico de custo divergem.
        var stub = new StubLlmProvider(SampleExercise(), CorrectionWith(ErrorCategory.Article));
        var detail = await Service(stub).CreatePracticeAsync(CreateRequest());
        await Service(stub).CorrectPracticeAsync(detail.Id, "my translation");

        var outcome = await Service(stub).DeletePracticeAsync(detail.Id);

        Assert.Equal(PracticeOutcome.Ok, outcome);
        await using var ctx = _db.NewContext();
        Assert.Equal(0, ctx.Exercises.Count());
        Assert.Equal(2, ctx.LlmCalls.Count()); // o custo sobrevive à exclusão
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
    }

    public void Dispose() => _db.Dispose();
}
