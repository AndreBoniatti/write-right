using WriteRight.Api.Data;
using WriteRight.Api.Llm;
using WriteRight.Api.Services;
using WriteRight.Shared;
using WriteRight.Shared.Analysis;
using WriteRight.Shared.Practices;
using WriteRight.Shared.Taxonomy;
using WriteRight.Shared.Usage;
using WriteRight.Tests.Support;

namespace WriteRight.Tests.Services;

/// <summary>
/// A análise só vale se ela não puder inventar. Estes testes travam as duas coisas
/// que sustentam isso: a <b>conferência de evidência</b> (o modelo cita ids; id que
/// não foi enviado não vira padrão) e a <b>janela por volume</b> (detectar padrão
/// exige mais material do que contar categoria — por isso ela não é a janela de 5
/// do perfil). Tudo contra SQLite real, com <see cref="StubLlmProvider"/> no lugar da IA.
/// </summary>
public sealed class AnalysisServiceTests : IDisposable
{
    private readonly TestDatabase _db = new();

    // UsageService compartilha o MESMO contexto do serviço (ele enfileira, o serviço
    // salva) — igual ao scoped do DI em produção.
    private AnalysisService Service(StubLlmProvider stub)
    {
        var ctx = _db.NewContext();
        return new(stub, ctx, new UsageService(ctx, TestPricing.Default()));
    }

    private static StubLlmProvider Stub(params DraftPattern[] patterns) =>
        StubWith(new[] { new AnalysisStudyItem(StudyItemKind.Rule, "in/on com tempo", "Use 'on' com dias.") }, patterns);

    private static StubLlmProvider StubWith(
        IReadOnlyList<AnalysisStudyItem> studyItems, params DraftPattern[] patterns) =>
        new(analysis: new AnalysisDraft(patterns, studyItems));

    /// <summary>Semeia uma prática concluída com os erros dados; devolve os ids gerados.</summary>
    private async Task<List<int>> SeedAsync(DateTimeOffset completedAt, params (ErrorCategory Cat, ErrorSeverity Sev)[] errors)
    {
        await using var ctx = _db.NewContext();
        var practice = new ExerciseAttempt
        {
            SourceLanguage = Language.Portuguese,
            TargetLanguage = Language.English,
            Status = PracticeStatus.Completed,
            SourceText = "seed",
            UserTranslation = "seed",
            CreatedAt = completedAt,
            CompletedAt = completedAt,
            Errors = errors.Select((e, i) => new ExerciseError
            {
                Category = e.Cat,
                Severity = e.Sev,
                Original = $"errado{i}",
                Correction = $"certo{i}",
                Explanation = $"porquê{i}",
            }).ToList(),
        };
        ctx.Exercises.Add(practice);
        await ctx.SaveChangesAsync();
        return practice.Errors.Select(e => e.Id).ToList();
    }

    /// <summary>N erros da mesma categoria — atalho pros testes de volume.</summary>
    private static (ErrorCategory, ErrorSeverity)[] Errors(int count, ErrorCategory category = ErrorCategory.Preposition) =>
        Enumerable.Repeat((category, ErrorSeverity.Understandable), count).ToArray();

    /// <summary>Histórico acima do piso: 5 práticas × 4 erros = 20 erros.</summary>
    private async Task<List<int>> SeedAboveFloorAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var ids = new List<int>();
        for (var i = 0; i < AnalysisService.MinPractices; i++)
            ids.AddRange(await SeedAsync(now.AddMinutes(-i), Errors(4)));
        return ids;
    }

    // ── Piso de dados ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStateAsync_on_empty_db_is_NotEnoughData()
    {
        var state = await Service(new StubLlmProvider()).GetStateAsync();

        Assert.Null(state.Latest);
        Assert.Equal(AnalysisGate.NotEnoughData, state.Gate);
        Assert.Equal(0, state.CompletedPractices);
        Assert.Equal(0, state.TotalErrors);
    }

    [Fact]
    public async Task GenerateAsync_below_practice_floor_does_not_call_the_ai()
    {
        // Erros de sobra, mas poucas práticas: o volume não compensa a amostra curta.
        await SeedAsync(DateTimeOffset.UtcNow, Errors(30));

        var stub = new StubLlmProvider();
        var (outcome, analysis) = await Service(stub).GenerateAsync();

        Assert.Equal(AnalysisOutcome.NotEnoughData, outcome);
        Assert.Null(analysis);
        Assert.Null(stub.LastAnalysisRequest); // nem chegou a chamar
        await using var ctx = _db.NewContext();
        Assert.Equal(0, ctx.Analyses.Count());
    }

    [Fact]
    public async Task GenerateAsync_below_error_floor_does_not_call_the_ai()
    {
        // Práticas suficientes, mas quase sem erro: nada pra diagnosticar.
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < AnalysisService.MinPractices + 1; i++)
            await SeedAsync(now.AddMinutes(-i), Errors(1));

        var stub = new StubLlmProvider();
        Assert.Equal(AnalysisOutcome.NotEnoughData, (await Service(stub).GenerateAsync()).Outcome);
        Assert.Null(stub.LastAnalysisRequest);
    }

    // ── Conferência de evidência (a trava central) ───────────────────────────

    [Fact]
    public async Task GenerateAsync_grounds_pattern_in_real_errors()
    {
        var ids = await SeedAboveFloorAsync();
        var stub = Stub(new DraftPattern("Preposição de tempo", "Você troca in por on.", ids.Take(3).ToList()));

        var (outcome, analysis) = await Service(stub).GenerateAsync();

        Assert.Equal(AnalysisOutcome.Ok, outcome);
        var pattern = Assert.Single(analysis!.Patterns);
        Assert.Equal("Preposição de tempo", pattern.Title);
        Assert.Equal(3, pattern.Evidence.Count);
        // A evidência é o erro REAL do banco, não texto reescrito pelo modelo.
        Assert.All(pattern.Evidence, e => Assert.StartsWith("errado", e.Original));
        Assert.All(pattern.Evidence, e => Assert.True(e.PracticeId > 0));
    }

    [Fact]
    public async Task GenerateAsync_drops_pattern_citing_ids_that_were_never_sent()
    {
        var ids = await SeedAboveFloorAsync();
        var stub = Stub(
            new DraftPattern("Inventado", "Sem lastro.", new List<int> { 90001, 90002, 90003 }),
            new DraftPattern("Real", "Com lastro.", ids.Take(3).ToList()));

        var (outcome, analysis) = await Service(stub).GenerateAsync();

        Assert.Equal(AnalysisOutcome.Ok, outcome);
        var pattern = Assert.Single(analysis!.Patterns);
        Assert.Equal("Real", pattern.Title); // o inventado não sobreviveu
    }

    [Fact]
    public async Task GenerateAsync_drops_pattern_below_minimum_evidence()
    {
        var ids = await SeedAboveFloorAsync();
        var thin = ids.Take(AnalysisService.MinEvidence - 1).ToList();
        var stub = Stub(
            new DraftPattern("Coincidência", "Poucos casos.", thin),
            new DraftPattern("Padrão", "Casos suficientes.", ids.Take(AnalysisService.MinEvidence).ToList()));

        var analysis = (await Service(stub).GenerateAsync()).Analysis;

        var pattern = Assert.Single(analysis!.Patterns);
        Assert.Equal("Padrão", pattern.Title);
    }

    [Fact]
    public async Task GenerateAsync_deduplicates_repeated_evidence_ids()
    {
        var ids = await SeedAboveFloorAsync();
        // Citar o mesmo erro três vezes não constrói um padrão.
        var repeated = new List<int> { ids[0], ids[0], ids[0] };
        var stub = Stub(new DraftPattern("Repetido", "Mesmo erro 3×.", repeated));

        var (outcome, analysis) = await Service(stub).GenerateAsync();

        Assert.Equal(AnalysisOutcome.NoGrounding, outcome);
        Assert.Null(analysis);
    }

    [Fact]
    public async Task GenerateAsync_without_any_grounded_pattern_persists_nothing()
    {
        await SeedAboveFloorAsync();
        var stub = Stub(new DraftPattern("Tudo inventado", "x", new List<int> { 91, 92, 93 }));

        var (outcome, analysis) = await Service(stub).GenerateAsync();

        Assert.Equal(AnalysisOutcome.NoGrounding, outcome);
        Assert.Null(analysis);
        await using var ctx = _db.NewContext();
        Assert.Equal(0, ctx.Analyses.Count()); // falha da chamada, não diagnóstico
    }

    [Fact]
    public async Task GenerateAsync_caps_patterns_at_the_ceiling()
    {
        var now = DateTimeOffset.UtcNow;
        var ids = new List<int>();
        for (var i = 0; i < AnalysisService.MinPractices; i++)
            ids.AddRange(await SeedAsync(now.AddMinutes(-i), Errors(10)));

        // Mais padrões do que o teto, todos com lastro válido.
        var drafts = Enumerable.Range(0, AnalysisService.MaxPatterns + 3)
            .Select(i => new DraftPattern($"Padrão {i}", "d", ids.Skip(i * 3).Take(3).ToList()))
            .ToArray();

        var analysis = (await Service(Stub(drafts)).GenerateAsync()).Analysis;

        Assert.Equal(AnalysisService.MaxPatterns, analysis!.Patterns.Count);
        Assert.Equal("Padrão 0", analysis.Patterns[0].Title); // mantém a ordem do modelo
    }

    [Fact]
    public async Task GenerateAsync_caps_study_items_and_drops_empty_ones()
    {
        // O schema não limita cardinalidade (a API rejeita minItems/maxItems em array),
        // então o teto de itens de estudo também é responsabilidade do servidor.
        var ids = await SeedAboveFloorAsync();
        var items = Enumerable.Range(0, AnalysisService.MaxStudyItems + 3)
            .Select(i => new AnalysisStudyItem(StudyItemKind.Topic, $"Tema {i}", "conteúdo"))
            .Append(new AnalysisStudyItem(StudyItemKind.Rule, "  ", "sem título"))
            .ToList();

        var stub = StubWith(items, new DraftPattern("p", "d", ids.Take(3).ToList()));
        var analysis = (await Service(stub).GenerateAsync()).Analysis;

        Assert.Equal(AnalysisService.MaxStudyItems, analysis!.StudyItems.Count);
        Assert.DoesNotContain(analysis.StudyItems, i => string.IsNullOrWhiteSpace(i.Title));
    }

    [Fact]
    public async Task GenerateAsync_derives_categories_from_the_evidence()
    {
        var now = DateTimeOffset.UtcNow;
        var ids = new List<int>();
        for (var i = 0; i < AnalysisService.MinPractices; i++)
        {
            ids.AddRange(await SeedAsync(now.AddMinutes(-i),
                (ErrorCategory.LiteralTranslation, ErrorSeverity.BreaksMeaning),
                (ErrorCategory.WordOrder, ErrorSeverity.Understandable),
                (ErrorCategory.WordOrder, ErrorSeverity.Understandable),
                (ErrorCategory.Naturalness, ErrorSeverity.Polish)));
        }

        // O modelo nunca declara categoria — ela sai dos erros citados.
        var stub = Stub(new DraftPattern("Traduz palavra a palavra", "d", ids.Take(3).ToList()));
        var analysis = (await Service(stub).GenerateAsync()).Analysis;

        var pattern = Assert.Single(analysis!.Patterns);
        Assert.Equal(
            pattern.Evidence.Select(e => e.Category).Distinct().OrderBy(c => c),
            pattern.Categories.OrderBy(c => c));
        Assert.Contains(ErrorCategory.LiteralTranslation, pattern.Categories);
    }

    // ── Janela ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_window_is_not_capped_at_the_profile_recent_five()
    {
        // 8 práticas pequenas: longe do orçamento, então TODAS entram. É a diferença
        // deliberada pro recorte "recente" do perfil, que para em 5.
        var now = DateTimeOffset.UtcNow;
        var ids = new List<int>();
        for (var i = 0; i < 8; i++)
            ids.AddRange(await SeedAsync(now.AddMinutes(-i), Errors(3)));

        var stub = Stub(new DraftPattern("p", "d", ids.Take(3).ToList()));
        var analysis = (await Service(stub).GenerateAsync()).Analysis;

        Assert.Equal(8, analysis!.PracticesAnalyzed);
        Assert.Equal(24, stub.LastAnalysisRequest!.Errors.Count);
    }

    [Fact]
    public async Task GenerateAsync_window_stops_once_the_error_budget_is_met()
    {
        // Cada prática já cobre mais da metade do orçamento → duas bastam, mesmo
        // havendo histórico de sobra acima do piso.
        var perPractice = (AnalysisService.ErrorBudget / 2) + 1;
        var now = DateTimeOffset.UtcNow;
        var ids = new List<int>();
        for (var i = 0; i < AnalysisService.MinPractices + 1; i++)
            ids.AddRange(await SeedAsync(now.AddMinutes(-i), Errors(perPractice)));

        var stub = Stub(new DraftPattern("p", "d", ids.Take(3).ToList()));
        var analysis = (await Service(stub).GenerateAsync()).Analysis;

        Assert.Equal(2, analysis!.PracticesAnalyzed); // parou; não mandou o histórico todo
        Assert.Equal(perPractice * 2, stub.LastAnalysisRequest!.Errors.Count);
    }

    [Fact]
    public async Task GenerateAsync_sends_only_the_heaviest_categories()
    {
        // 8 categorias na janela; as duas mais leves devem ficar de fora do que vai ao modelo.
        var heavy = new[]
        {
            ErrorCategory.Preposition, ErrorCategory.VerbTense, ErrorCategory.Article,
            ErrorCategory.WordChoice, ErrorCategory.Spelling, ErrorCategory.Pronoun,
        };
        var now = DateTimeOffset.UtcNow;
        var ids = new List<int>();
        for (var i = 0; i < AnalysisService.MinPractices; i++)
        {
            var errors = heavy.SelectMany(c => Errors(3, c))
                .Append((ErrorCategory.Capitalization, ErrorSeverity.Polish))
                .Append((ErrorCategory.Punctuation, ErrorSeverity.Polish))
                .ToArray();
            ids.AddRange(await SeedAsync(now.AddMinutes(-i), errors));
        }

        var stub = Stub(new DraftPattern("p", "d", ids.Take(3).ToList()));
        await Service(stub).GenerateAsync();

        var sent = stub.LastAnalysisRequest!.Errors.Select(e => e.Category).Distinct().ToList();
        Assert.Equal(AnalysisService.TopCategories, sent.Count);
        Assert.DoesNotContain(ErrorCategory.Capitalization, sent);
        Assert.DoesNotContain(ErrorCategory.Punctuation, sent);
    }

    [Fact]
    public async Task GenerateAsync_sends_a_lifetime_aggregate_that_reaches_beyond_the_window()
    {
        // Uma prática antiga só de ortografia, fora da janela; e recentes pesadas o
        // bastante pra estourar o orçamento antes de chegar nela.
        await SeedAsync(DateTimeOffset.UtcNow.AddDays(-30), Errors(5, ErrorCategory.Spelling));

        var perPractice = (AnalysisService.ErrorBudget / 2) + 1;
        var now = DateTimeOffset.UtcNow;
        var recent = new List<int>();
        for (var i = 0; i < AnalysisService.MinPractices; i++)
            recent.AddRange(await SeedAsync(now.AddMinutes(-i), Errors(perPractice)));

        var stub = Stub(new DraftPattern("p", "d", recent.Take(3).ToList()));
        var analysis = (await Service(stub).GenerateAsync()).Analysis;

        var request = stub.LastAnalysisRequest!;
        Assert.Equal(2, analysis!.PracticesAnalyzed);                       // janela curta
        Assert.All(request.Errors, e => Assert.Equal(ErrorCategory.Preposition, e.Category));

        // …mas o agregado ainda mostra o que ficou de fora: é o mapa do todo, barato.
        Assert.Contains(request.LifetimeByCategory, c => c.Category == ErrorCategory.Spelling);
        Assert.Equal(AnalysisService.MinPractices + 1, request.LifetimePractices);
    }

    // ── Persistência ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStateAsync_returns_the_persisted_analysis_intact()
    {
        var ids = await SeedAboveFloorAsync();
        var stub = Stub(new DraftPattern("Preposição de tempo", "Você troca in por on.", ids.Take(3).ToList()));
        await Service(stub).GenerateAsync();

        // Contexto novo: prova que foi ao banco e voltou (round-trip do JSON).
        var state = await Service(new StubLlmProvider()).GetStateAsync();

        var pattern = Assert.Single(state.Latest!.Patterns);
        Assert.Equal("Preposição de tempo", pattern.Title);
        Assert.Equal("Você troca in por on.", pattern.Diagnosis);
        Assert.Equal(3, pattern.Evidence.Count);
        Assert.Equal(ErrorSeverity.Understandable, pattern.Evidence[0].Severity);
        var study = Assert.Single(state.Latest.StudyItems);
        Assert.Equal(StudyItemKind.Rule, study.Kind);
    }

    [Fact]
    public async Task Analysis_survives_deletion_of_the_practice_it_cited()
    {
        var ids = await SeedAboveFloorAsync();
        var stub = Stub(new DraftPattern("p", "d", ids.Take(3).ToList()));
        await Service(stub).GenerateAsync();

        // Apaga TODAS as práticas: a evidência é snapshot, não ponteiro.
        await using (var ctx = _db.NewContext())
        {
            ctx.Exercises.RemoveRange(ctx.Exercises);
            await ctx.SaveChangesAsync();
        }

        var state = await Service(new StubLlmProvider()).GetStateAsync();

        Assert.Equal(3, state.Latest!.Patterns[0].Evidence.Count);
        Assert.StartsWith("errado", state.Latest.Patterns[0].Evidence[0].Original);
    }

    // ── Gate de regeração ────────────────────────────────────────────────────

    [Fact]
    public async Task GetStateAsync_is_UpToDate_right_after_generating()
    {
        var ids = await SeedAboveFloorAsync();
        await Service(Stub(new DraftPattern("p", "d", ids.Take(3).ToList()))).GenerateAsync();

        var state = await Service(new StubLlmProvider()).GetStateAsync();

        Assert.Equal(AnalysisGate.UpToDate, state.Gate);
        Assert.Equal(0, state.NewPracticesSinceLatest);
    }

    [Fact]
    public async Task GetStateAsync_turns_Ready_again_after_enough_new_practices()
    {
        var ids = await SeedAboveFloorAsync();
        await Service(Stub(new DraftPattern("p", "d", ids.Take(3).ToList()))).GenerateAsync();

        // Três práticas novas, depois da análise.
        var later = DateTimeOffset.UtcNow.AddHours(1);
        for (var i = 0; i < 3; i++)
            await SeedAsync(later.AddMinutes(i), Errors(2));

        var state = await Service(new StubLlmProvider()).GetStateAsync();

        Assert.Equal(AnalysisGate.Ready, state.Gate);
        Assert.Equal(3, state.NewPracticesSinceLatest);
    }

    [Fact]
    public async Task GenerateAsync_is_allowed_even_when_UpToDate()
    {
        // O gate é conselho da UI, não proibição: quem paga a chamada decide.
        var ids = await SeedAboveFloorAsync();
        await Service(Stub(new DraftPattern("primeira", "d", ids.Take(3).ToList()))).GenerateAsync();

        var (outcome, analysis) = await Service(
            Stub(new DraftPattern("segunda", "d", ids.Take(3).ToList()))).GenerateAsync();

        Assert.Equal(AnalysisOutcome.Ok, outcome);
        Assert.Equal("segunda", analysis!.Patterns[0].Title);

        // E a mais nova é a que a tela passa a mostrar.
        var state = await Service(new StubLlmProvider()).GetStateAsync();
        Assert.Equal("segunda", state.Latest!.Patterns[0].Title);
        await using var ctx = _db.NewContext();
        Assert.Equal(2, ctx.Analyses.Count()); // histórico preservado
    }

    // ── Consumo ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_records_usage_linked_to_the_analysis()
    {
        var ids = await SeedAboveFloorAsync();

        var (outcome, analysis) = await Service(
            Stub(new DraftPattern("t", "d", ids.Take(3).ToList()))).GenerateAsync();

        Assert.Equal(AnalysisOutcome.Ok, outcome);

        await using var ctx = _db.NewContext();
        var call = Assert.Single(ctx.LlmCalls);
        Assert.Equal(LlmOperation.Analysis, call.Operation);
        Assert.Equal(analysis!.Id, call.AnalysisId);
        Assert.Null(call.PracticeId);
        Assert.Equal(StubLlmProvider.DefaultUsage.OutputTokens, call.OutputTokens);
        Assert.True(call.CostUsd > 0);
    }

    [Fact]
    public async Task GenerateAsync_records_usage_even_when_nothing_is_grounded()
    {
        // A chamada sem lastro não persiste análise nenhuma — mas FOI cobrada.
        // É exatamente o gasto que some de qualquer instrumentação presa ao resultado.
        await SeedAboveFloorAsync();
        var stub = Stub(new DraftPattern("Tudo inventado", "x", new List<int> { 91, 92, 93 }));

        var (outcome, _) = await Service(stub).GenerateAsync();

        Assert.Equal(AnalysisOutcome.NoGrounding, outcome);

        await using var ctx = _db.NewContext();
        Assert.Equal(0, ctx.Analyses.Count());
        var call = Assert.Single(ctx.LlmCalls);
        Assert.Equal(LlmOperation.Analysis, call.Operation);
        Assert.Null(call.AnalysisId); // não há análise pra apontar — o gasto existe assim mesmo
        Assert.True(call.CostUsd > 0);
    }

    [Fact]
    public async Task GenerateAsync_persists_even_when_the_client_gives_up_mid_call()
    {
        // A análise é a chamada mais lenta do app — é ela que estourou o timeout de
        // 100s do cliente na prática. Ela é cara: descartar por desistência do
        // navegador significaria pagar duas vezes pelo mesmo diagnóstico.
        var ids = await SeedAboveFloorAsync();
        var stub = Stub(new DraftPattern("t", "d", ids.Take(3).ToList()));

        var browserGaveUp = new CancellationTokenSource();
        stub.CancelDuringCall = browserGaveUp;

        var (outcome, analysis) = await Service(stub).GenerateAsync(browserGaveUp.Token);

        Assert.Equal(AnalysisOutcome.Ok, outcome);

        await using var ctx = _db.NewContext();
        Assert.Equal(1, ctx.Analyses.Count());
        var call = Assert.Single(ctx.LlmCalls);
        Assert.Equal(analysis!.Id, call.AnalysisId);
        Assert.True(call.CostUsd > 0);
    }

    [Fact]
    public async Task GenerateAsync_records_usage_when_the_call_is_billed_but_fails()
    {
        // Cobrado e a resposta nem foi lida (recusa, JSON truncado). Nada de análise,
        // mas o gasto fica — mesmo raciocínio do NoGrounding, motivo diferente.
        await SeedAboveFloorAsync();
        var stub = Stub(new DraftPattern("t", "d", new List<int> { 1, 2, 3 }));
        stub.FailAfterBilling = true;

        await Assert.ThrowsAsync<LlmCallFailedException>(() => Service(stub).GenerateAsync());

        await using var ctx = _db.NewContext();
        Assert.Equal(0, ctx.Analyses.Count());
        var call = Assert.Single(ctx.LlmCalls);
        Assert.Equal(LlmOperation.Analysis, call.Operation);
        Assert.Null(call.AnalysisId);
        Assert.True(call.CostUsd > 0);
    }

    [Fact]
    public async Task GenerateAsync_below_the_floor_spends_nothing()
    {
        // Barrado antes da IA: nenhuma chamada, nenhum registro de consumo.
        await SeedAsync(DateTimeOffset.UtcNow, Errors(3));

        var (outcome, _) = await Service(new StubLlmProvider()).GenerateAsync();

        Assert.Equal(AnalysisOutcome.NotEnoughData, outcome);
        await using var ctx = _db.NewContext();
        Assert.Equal(0, ctx.LlmCalls.Count());
    }

    public void Dispose() => _db.Dispose();
}
