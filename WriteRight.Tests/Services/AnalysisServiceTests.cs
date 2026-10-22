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
    /// <summary>
    /// A fila é do teste inteiro (como o singleton em produção), pra os testes de
    /// estado do job enxergarem o que o enfileiramento fez.
    /// </summary>
    private readonly AnalysisJobQueue _jobs = new();

    private AnalysisService Service(StubLlmProvider stub)
    {
        var ctx = _db.NewContext();
        return new(stub, ctx, new UsageService(ctx, TestPricing.Default()), _jobs);
    }

    private static StubLlmProvider Stub(params DraftPattern[] patterns) =>
        new(analysis: new AnalysisDraft(patterns));

    private static readonly AnalysisStudyItem DefaultStudy =
        new(StudyItemKind.Rule, "in/on com tempo", "Use 'on' com dias.");

    private static DraftPattern Draft(
        string title, string diagnosis, IReadOnlyList<int> evidenceIds, AnalysisStudyItem? study = null) =>
        new(title, diagnosis, evidenceIds, study ?? DefaultStudy);

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
        var stub = Stub(Draft("Preposição de tempo", "Você troca in por on.", ids.Take(3).ToList()));

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
            Draft("Inventado", "Sem lastro.", new List<int> { 90001, 90002, 90003 }),
            Draft("Real", "Com lastro.", ids.Take(3).ToList()));

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
            Draft("Coincidência", "Poucos casos.", thin),
            Draft("Padrão", "Casos suficientes.", ids.Take(AnalysisService.MinEvidence).ToList()));

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
        var stub = Stub(Draft("Repetido", "Mesmo erro 3×.", repeated));

        var (outcome, analysis) = await Service(stub).GenerateAsync();

        Assert.Equal(AnalysisOutcome.NoGrounding, outcome);
        Assert.Null(analysis);
    }

    [Fact]
    public async Task GenerateAsync_without_any_grounded_pattern_persists_nothing()
    {
        await SeedAboveFloorAsync();
        var stub = Stub(Draft("Tudo inventado", "x", new List<int> { 91, 92, 93 }));

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
            .Select(i => Draft($"Padrão {i}", "d", ids.Skip(i * 3).Take(3).ToList()))
            .ToArray();

        var analysis = (await Service(Stub(drafts)).GenerateAsync()).Analysis;

        Assert.Equal(AnalysisService.MaxPatterns, analysis!.Patterns.Count);
        Assert.Equal("Padrão 0", analysis.Patterns[0].Title); // mantém a ordem do modelo
    }

    [Fact]
    public async Task GenerateAsync_keeps_each_study_item_with_its_own_pattern()
    {
        // O caso que quebrava o pareamento por posição: o padrão do meio cai na
        // conferência de evidência, e o estudo do terceiro NÃO pode escorregar pro lugar dele.
        var ids = await SeedAboveFloorAsync();
        var stub = Stub(
            Draft("Primeiro", "d", ids.Take(3).ToList(),
                new AnalysisStudyItem(StudyItemKind.Rule, "Regra do primeiro", "c1")),
            Draft("Inventado", "d", new List<int> { 90001, 90002, 90003 },
                new AnalysisStudyItem(StudyItemKind.Rule, "Regra do inventado", "c2")),
            Draft("Terceiro", "d", ids.Skip(3).Take(3).ToList(),
                new AnalysisStudyItem(StudyItemKind.Topic, "  Tema do terceiro  ", "  c3  ")));

        var analysis = (await Service(stub).GenerateAsync()).Analysis;

        Assert.Collection(analysis!.Patterns,
            p => Assert.Equal("Regra do primeiro", p.Study!.Title),
            p =>
            {
                Assert.Equal("Terceiro", p.Title);
                Assert.Equal(new AnalysisStudyItem(StudyItemKind.Topic, "Tema do terceiro", "c3"), p.Study);
            });
    }

    [Fact]
    public async Task GenerateAsync_drops_a_blank_study_item_but_keeps_the_pattern()
    {
        // O schema exige o item, não que ele venha preenchido. O diagnóstico com
        // lastro vale mesmo sem a regra; só não se mostra uma caixa vazia.
        var ids = await SeedAboveFloorAsync();
        var stub = new StubLlmProvider(analysis: new AnalysisDraft(new[]
        {
            new DraftPattern("Em branco", "d", ids.Take(3).ToList(),
                new AnalysisStudyItem(StudyItemKind.Rule, "  ", "sem título")),
            new DraftPattern("Ausente", "d", ids.Skip(3).Take(3).ToList(), null),
        }));

        var analysis = (await Service(stub).GenerateAsync()).Analysis;

        Assert.Equal(2, analysis!.Patterns.Count);
        Assert.All(analysis.Patterns, p => Assert.Null(p.Study));
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
        var stub = Stub(Draft("Traduz palavra a palavra", "d", ids.Take(3).ToList()));
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

        var stub = Stub(Draft("p", "d", ids.Take(3).ToList()));
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

        var stub = Stub(Draft("p", "d", ids.Take(3).ToList()));
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

        var stub = Stub(Draft("p", "d", ids.Take(3).ToList()));
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

        var stub = Stub(Draft("p", "d", recent.Take(3).ToList()));
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
        var stub = Stub(Draft("Preposição de tempo", "Você troca in por on.", ids.Take(3).ToList()));
        await Service(stub).GenerateAsync();

        // Contexto novo: prova que foi ao banco e voltou (round-trip do JSON).
        var state = await Service(new StubLlmProvider()).GetStateAsync();

        var pattern = Assert.Single(state.Latest!.Patterns);
        Assert.Equal("Preposição de tempo", pattern.Title);
        Assert.Equal("Você troca in por on.", pattern.Diagnosis);
        Assert.Equal(3, pattern.Evidence.Count);
        Assert.Equal(ErrorSeverity.Understandable, pattern.Evidence[0].Severity);
        Assert.Equal(DefaultStudy, pattern.Study);
    }

    [Fact]
    public async Task Analysis_survives_deletion_of_the_practice_it_cited()
    {
        var ids = await SeedAboveFloorAsync();
        var stub = Stub(Draft("p", "d", ids.Take(3).ToList()));
        await Service(stub).GenerateAsync();

        // Exclui TODAS as práticas — o que esconde os erros delas de toda consulta. A
        // evidência é snapshot, não ponteiro: a análise não depende deles pra se ler.
        await using (var ctx = _db.NewContext())
        {
            foreach (var practice in ctx.Exercises) practice.DeletedAt = DateTimeOffset.UtcNow;
            await ctx.SaveChangesAsync();
        }

        var state = await Service(new StubLlmProvider()).GetStateAsync();

        Assert.Equal(3, state.Latest!.Patterns[0].Evidence.Count);
        Assert.StartsWith("errado", state.Latest.Patterns[0].Evidence[0].Original);
    }

    /// <summary>
    /// Excluiu porque o texto não servia: os erros dele não podem virar evidência de
    /// um padrão novo, nem ajudar a passar o piso de dados.
    /// </summary>
    [Fact]
    public async Task A_deleted_practice_leaves_the_window_of_the_next_analysis()
    {
        var kept = await SeedAboveFloorAsync();
        var deleted = await SeedAsync(DateTimeOffset.UtcNow, Errors(4));
        await using (var ctx = _db.NewContext())
        {
            foreach (var practice in ctx.Exercises.Where(p => p.Errors.Any(e => deleted.Contains(e.Id))))
                practice.DeletedAt = DateTimeOffset.UtcNow;
            await ctx.SaveChangesAsync();
        }

        var stub = Stub(Draft("p", "d", kept.Take(3).ToList()));
        var analysis = (await Service(stub).GenerateAsync()).Analysis;

        Assert.Equal(AnalysisService.MinPractices, analysis!.PracticesAnalyzed);
        var sent = stub.LastAnalysisRequest!.Errors.Select(e => e.Id).ToList();
        Assert.Equal(kept.Count, sent.Count);
        Assert.DoesNotContain(sent, deleted.Contains);
    }

    [Fact]
    public async Task Deleted_practices_do_not_count_toward_the_data_floor()
    {
        await SeedAboveFloorAsync();
        await using (var ctx = _db.NewContext())
        {
            ctx.Exercises.First().DeletedAt = DateTimeOffset.UtcNow;
            await ctx.SaveChangesAsync();
        }

        var state = await Service(new StubLlmProvider()).GetStateAsync();

        Assert.Equal(AnalysisService.MinPractices - 1, state.CompletedPractices);
        Assert.Equal(AnalysisGate.NotEnoughData, state.Gate);
    }

    // ── Gate de regeração ────────────────────────────────────────────────────

    [Fact]
    public async Task GetStateAsync_is_UpToDate_right_after_generating()
    {
        var ids = await SeedAboveFloorAsync();
        await Service(Stub(Draft("p", "d", ids.Take(3).ToList()))).GenerateAsync();

        var state = await Service(new StubLlmProvider()).GetStateAsync();

        Assert.Equal(AnalysisGate.UpToDate, state.Gate);
        Assert.Equal(0, state.NewPracticesSinceLatest);
    }

    [Fact]
    public async Task GetStateAsync_turns_Ready_again_after_enough_new_practices()
    {
        var ids = await SeedAboveFloorAsync();
        await Service(Stub(Draft("p", "d", ids.Take(3).ToList()))).GenerateAsync();

        // Três práticas novas, depois da análise.
        var later = DateTimeOffset.UtcNow.AddHours(1);
        for (var i = 0; i < 3; i++)
            await SeedAsync(later.AddMinutes(i), Errors(2));

        var state = await Service(new StubLlmProvider()).GetStateAsync();

        Assert.Equal(AnalysisGate.Ready, state.Gate);
        Assert.Equal(3, state.NewPracticesSinceLatest);
    }

    // ── Quanto o histórico andou ─────────────────────────────────────────────

    [Fact]
    public async Task GetStateAsync_has_no_drift_without_an_analysis_or_new_material()
    {
        var ids = await SeedAboveFloorAsync();
        Assert.Null((await Service(new StubLlmProvider()).GetStateAsync()).Drift);

        await Service(Stub(Draft("p", "d", ids.Take(3).ToList()))).GenerateAsync();
        Assert.Null((await Service(new StubLlmProvider()).GetStateAsync()).Drift);
    }

    [Fact]
    public async Task GetStateAsync_compares_what_the_analysis_read_with_what_came_after()
    {
        // Janela: 20 erros de preposição. Depois: 20 erros, metade de ortografia.
        var ids = await SeedAboveFloorAsync();
        await Service(Stub(Draft("Preposição", "d", ids.Take(3).ToList()))).GenerateAsync();

        var later = DateTimeOffset.UtcNow.AddHours(1);
        for (var i = 0; i < 5; i++)
            await SeedAsync(later.AddMinutes(i), [.. Errors(2), .. Errors(2, ErrorCategory.Spelling)]);

        var drift = (await Service(new StubLlmProvider()).GetStateAsync()).Drift!;

        Assert.Equal(1.0, drift.CoveredShareRead, 6);
        Assert.Equal(0.5, drift.CoveredShareSince, 6);
        var top = Assert.Single(drift.TopUncoveredSince);
        Assert.Equal(new CategoryShare(ErrorCategory.Spelling, 0.5), top);
    }

    [Fact]
    public async Task A_practice_completed_during_the_call_counts_as_new_not_as_read()
    {
        // A tela diz que dá pra sair e seguir praticando enquanto a IA pensa. Uma prática
        // concluída nesse meio-tempo não foi ao modelo: tem que contar como nova.
        var ids = await SeedAboveFloorAsync();
        var stub = Stub(Draft("p", "d", ids.Take(3).ToList()));
        stub.DuringAnalysis = () => SeedAsync(DateTimeOffset.UtcNow, Errors(30, ErrorCategory.Spelling));

        await Service(stub).GenerateAsync();
        var state = await Service(new StubLlmProvider()).GetStateAsync();

        Assert.Equal(1, state.NewPracticesSinceLatest);
        // 20 lidos, 30 novos: a medida sai, e o "lido" segue só com o que foi ao modelo.
        Assert.Equal(1.0, state.Drift!.CoveredShareRead, 6);
        Assert.Equal(0.0, state.Drift.CoveredShareSince, 6);
    }

    [Fact]
    public async Task GetStateAsync_drift_reads_only_the_window_not_the_whole_past()
    {
        // Uma prática antiga só de ortografia fica FORA da janela (o orçamento estoura
        // antes). Ela não pode entrar no "antes": a análise nunca a leu.
        await SeedAsync(DateTimeOffset.UtcNow.AddDays(-30), Errors(5, ErrorCategory.Spelling));

        var perPractice = (AnalysisService.ErrorBudget / 2) + 1;
        var now = DateTimeOffset.UtcNow;
        var recent = new List<int>();
        for (var i = 0; i < AnalysisService.MinPractices; i++)
            recent.AddRange(await SeedAsync(now.AddMinutes(-i), Errors(perPractice)));

        var analysis = (await Service(Stub(Draft("p", "d", recent.Take(3).ToList()))).GenerateAsync()).Analysis!;
        Assert.Equal(2, analysis.PracticesAnalyzed);

        // Depois: o mesmo volume que a janela leu, metade de artigo.
        var later = DateTimeOffset.UtcNow.AddHours(1);
        for (var i = 0; i < 2; i++)
            await SeedAsync(later.AddMinutes(i), [.. Errors(46), .. Errors(45, ErrorCategory.Article)]);

        var drift = (await Service(new StubLlmProvider()).GetStateAsync()).Drift!;

        Assert.Equal(1.0, drift.CoveredShareRead, 6);
        Assert.Equal(92.0 / 182, drift.CoveredShareSince, 6);
        Assert.Equal(ErrorCategory.Article, Assert.Single(drift.TopUncoveredSince).Category);
    }

    [Fact]
    public async Task GenerateAsync_is_allowed_even_when_UpToDate()
    {
        // O gate é conselho da UI, não proibição: quem paga a chamada decide.
        var ids = await SeedAboveFloorAsync();
        await Service(Stub(Draft("primeira", "d", ids.Take(3).ToList()))).GenerateAsync();

        var (outcome, analysis) = await Service(
            Stub(Draft("segunda", "d", ids.Take(3).ToList()))).GenerateAsync();

        Assert.Equal(AnalysisOutcome.Ok, outcome);
        Assert.Equal("segunda", analysis!.Patterns[0].Title);

        // E a mais nova é a que a tela passa a mostrar.
        var state = await Service(new StubLlmProvider()).GetStateAsync();
        Assert.Equal("segunda", state.Latest!.Patterns[0].Title);
        await using var ctx = _db.NewContext();
        Assert.Equal(2, ctx.Analyses.Count()); // histórico preservado
    }

    // ── Piso síncrono / estado do job ────────────────────────────────────────

    [Fact]
    public async Task HasEnoughDataAsync_is_false_below_the_floor()
    {
        // Checado ANTES de enfileirar: enfileirar um job que já se sabe que vai
        // falhar faria o usuário esperar minutos pra receber a mesma negativa.
        await SeedAsync(DateTimeOffset.UtcNow, Errors(3));

        Assert.False(await Service(new StubLlmProvider()).HasEnoughDataAsync());
    }

    [Fact]
    public async Task HasEnoughDataAsync_is_true_above_the_floor()
    {
        await SeedAboveFloorAsync();

        Assert.True(await Service(new StubLlmProvider()).HasEnoughDataAsync());
    }

    [Fact]
    public async Task GetStateAsync_surfaces_the_running_job()
    {
        // É esse campo que a tela usa pra decidir entre "Analisando…" e o botão —
        // sem ele o usuário recarrega a página e perde de vista a geração em curso.
        await SeedAboveFloorAsync();
        _jobs.TryEnqueue();

        var state = await Service(new StubLlmProvider()).GetStateAsync();

        Assert.Equal(AnalysisJobStatus.Running, state.Job.Status);
    }

    [Fact]
    public async Task GetStateAsync_surfaces_the_failure_reason()
    {
        await SeedAboveFloorAsync();
        _jobs.TryEnqueue();
        _jobs.MarkFailed("a IA respondeu sem evidência válida");

        var state = await Service(new StubLlmProvider()).GetStateAsync();

        Assert.Equal(AnalysisJobStatus.Failed, state.Job.Status);
        Assert.Equal("a IA respondeu sem evidência válida", state.Job.FailureReason);
    }

    // ── Consumo ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_records_usage_linked_to_the_analysis()
    {
        var ids = await SeedAboveFloorAsync();

        var (outcome, analysis) = await Service(
            Stub(Draft("t", "d", ids.Take(3).ToList()))).GenerateAsync();

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
        var stub = Stub(Draft("Tudo inventado", "x", new List<int> { 91, 92, 93 }));

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
        var stub = Stub(Draft("t", "d", ids.Take(3).ToList()));

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
        var stub = Stub(Draft("t", "d", new List<int> { 1, 2, 3 }));
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
