using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using WriteRight.Api.Data;
using WriteRight.Api.Llm;
using WriteRight.Shared.Analysis;
using WriteRight.Shared.Practices;
using WriteRight.Shared.Profile;
using WriteRight.Shared.Taxonomy;
using WriteRight.Shared.Usage;

namespace WriteRight.Api.Services;

/// <summary>Desfecho de uma tentativa de gerar análise.</summary>
public enum AnalysisOutcome
{
    Ok,

    /// <summary>Histórico pequeno demais — analisar agora produziria padrão inventado.</summary>
    NotEnoughData,

    /// <summary>
    /// O modelo respondeu, mas nenhum padrão sobreviveu à conferência de evidência.
    /// É falha da chamada, não diagnóstico: nada é persistido.
    /// </summary>
    NoGrounding,
}

/// <summary>
/// Gera e guarda a análise de fraquezas: diagnóstico do estado atual, montado a
/// partir dos erros reais do usuário.
///
/// Três decisões sustentam o resto:
///  • <b>Janela por volume, não por número de práticas.</b> Contar erro por categoria
///    é honesto com pouca amostra; DETECTAR PADRÃO não é — precisa de várias ocorrências
///    da mesma sub-regra. A janela anda do mais recente pro mais antigo até juntar
///    <see cref="ErrorBudget"/> erros, então se ajusta sozinha conforme o usuário melhora.
///  • <b>Nenhuma afirmação sem lastro.</b> O modelo cita ids; aqui se confere contra o
///    conjunto enviado e se descarta o que não bate. É a trava contra o conselho genérico.
///  • <b>Evidência vira snapshot.</b> A análise guarda o texto do erro, não FK — segue
///    íntegra mesmo que a prática de origem seja excluída depois.
/// </summary>
public sealed class AnalysisService
{
    /// <summary>Piso pra habilitar a geração: abaixo disso, "padrão" é ruído com cara de conclusão.</summary>
    public const int MinPractices = 5;
    public const int MinErrors = 15;

    /// <summary>Práticas novas a partir das quais vale regerar (abaixo disso é gastar à toa).</summary>
    private const int NewPracticesToRefresh = 3;

    // internal (não private) pra os testes afirmarem contra a constante e não contra
    // um número copiado — assim ajustar a régua não deixa teste mentindo.

    /// <summary>Alvo de erros na janela — limita o custo e garante material pra padrão.</summary>
    internal const int ErrorBudget = 180;

    /// <summary>Só as categorias mais pesadas da janela vão pro modelo; cauda longa é ruído caro.</summary>
    internal const int TopCategories = 6;

    /// <summary>Teto de padrões. O piso importa mais: 1 padrão bem lastreado &gt; 5 esticados.</summary>
    internal const int MaxPatterns = 5;

    /// <summary>Erros citados mínimos pra um padrão valer. Menos que isso é coincidência.</summary>
    internal const int MinEvidence = 3;

    internal const int MaxStudyItems = 6;

    /// <summary>
    /// (De)serialização do corpo da análise. Enums como STRING pelo mesmo motivo do
    /// resto do schema: o JSON no banco continua legível a olho nu.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILlmProvider _llm;
    private readonly WriteRightDbContext _db;
    private readonly UsageService _usage;
    private readonly AnalysisJobQueue _jobs;

    public AnalysisService(
        ILlmProvider llm, WriteRightDbContext db, UsageService usage, AnalysisJobQueue jobs)
    {
        _llm = llm;
        _db = db;
        _usage = usage;
        _jobs = jobs;
    }

    /// <summary>
    /// Há material suficiente pra uma análise honesta? Checado de forma SÍNCRONA
    /// antes de enfileirar: é leitura barata, e enfileirar um job que já se sabe que
    /// vai falhar só faria o usuário esperar pra receber a mesma negativa.
    /// </summary>
    public async Task<bool> HasEnoughDataAsync(CancellationToken ct = default)
    {
        var (practices, errors) = await LoadCompletedAsync(ct);
        return practices.Count >= MinPractices && errors.Count >= MinErrors;
    }

    /// <summary>
    /// Estado da tela: a última análise e se vale gerar outra. Os contadores vão
    /// junto pra UI explicar o motivo, em vez de só desabilitar o botão sem dizer nada.
    /// </summary>
    public async Task<AnalysisState> GetStateAsync(CancellationToken ct = default)
    {
        var (practices, errors) = await LoadCompletedAsync(ct);
        var latest = await LoadLatestAsync(ct);

        var newSince = latest is null
            ? practices.Count
            : practices.Count(p => p.CompletedAt > latest.GeneratedAt);

        var gate = Gate(practices.Count, errors.Count, latest, newSince);

        return new AnalysisState(
            latest is null ? null : ToContract(latest),
            gate,
            practices.Count,
            errors.Count,
            newSince,
            MinPractices,
            MinErrors,
            _jobs.Current);
    }

    /// <summary>
    /// Gera uma análise nova e persiste. Só o piso de dados bloqueia
    /// (<see cref="AnalysisOutcome.NotEnoughData"/>): <see cref="AnalysisGate.UpToDate"/>
    /// é conselho da UI, não proibição — quem paga a chamada é o usuário, e insistir
    /// numa releitura é decisão dele.
    /// </summary>
    public async Task<(AnalysisOutcome Outcome, WeaknessAnalysis? Analysis)> GenerateAsync(
        CancellationToken ct = default)
    {
        var (practices, errors) = await LoadCompletedAsync(ct);
        if (practices.Count < MinPractices || errors.Count < MinErrors)
            return (AnalysisOutcome.NotEnoughData, null);

        var window = BuildWindow(practices, errors);

        var request = new AnalysisRequest(
            window.Errors.Select(e => new AnalysisErrorRow(
                e.Id, e.Category, e.Severity, e.Original, e.Correction, e.Explanation)).ToList(),
            LifetimeByCategory(errors),
            window.Practices,
            practices.Count,
            MaxPatterns,
            MinEvidence,
            MaxStudyItems);

        LlmResult<AnalysisDraft> result;
        try
        {
            result = await _llm.AnalyzeAsync(request, ct);
        }
        catch (LlmCallFailedException ex)
        {
            // Mesmo raciocínio do NoGrounding logo abaixo: cobrado sem produzir
            // análise. A diferença é só o motivo — aqui a resposta nem foi lida.
            _usage.Record(LlmOperation.Analysis, ex.Usage);
            await _db.SaveChangesAsync(UsageService.AfterBilling);
            throw;
        }

        var patterns = Ground(result.Value.Patterns, window.Errors);
        if (patterns.Count == 0)
        {
            // Nada é persistido como análise — mas a chamada FOI cobrada. O consumo
            // é gravado assim mesmo (sem AnalysisId): esta é exatamente a chamada
            // que some de qualquer instrumentação presa ao resultado.
            _usage.Record(LlmOperation.Analysis, result.Usage);
            await _db.SaveChangesAsync(UsageService.AfterBilling);
            return (AnalysisOutcome.NoGrounding, null);
        }

        var record = new AnalysisRecord
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            PracticesAnalyzed = window.Practices,
            ErrorsAnalyzed = window.Errors.Count,
            PatternsJson = JsonSerializer.Serialize(patterns, Json),
            StudyItemsJson = JsonSerializer.Serialize(StudyItems(result.Value.StudyItems), Json),
        };

        // A análise em si também é gravada com o token não cancelável: ela já foi
        // paga. Se o navegador desistiu no meio, o usuário a encontra ao recarregar,
        // em vez de pagar de novo pela mesma análise.
        _db.Analyses.Add(record);
        await _db.SaveChangesAsync(UsageService.AfterBilling);

        // Segundo save: o Id da análise só existe depois do primeiro (mesmo motivo
        // do CreatePracticeAsync).
        _usage.Record(LlmOperation.Analysis, result.Usage, analysisId: record.Id);
        await _db.SaveChangesAsync(UsageService.AfterBilling);

        return (AnalysisOutcome.Ok, ToContract(record));
    }

    // ── Janela ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Escolhe o material que vai pro modelo: caminha das práticas concluídas mais
    /// recentes pras mais antigas até juntar <see cref="ErrorBudget"/> erros, depois
    /// mantém só as <see cref="TopCategories"/> categorias de maior peso dessa janela.
    /// Categoria com dois erros não sustenta padrão nenhum — só gastaria contexto.
    /// </summary>
    private static (int Practices, IReadOnlyList<ErrorRecord> Errors) BuildWindow(
        IReadOnlyList<CompletedPractice> practices, IReadOnlyList<ErrorRecord> errors)
    {
        var byPractice = errors.ToLookup(e => e.PracticeId);

        var windowIds = new HashSet<int>();
        var budget = 0;
        foreach (var practice in practices.OrderByDescending(p => p.CompletedAt))
        {
            windowIds.Add(practice.Id);
            budget += byPractice[practice.Id].Count();
            if (budget >= ErrorBudget) break;
        }

        var inWindow = errors.Where(e => windowIds.Contains(e.PracticeId)).ToList();

        var top = inWindow
            .GroupBy(e => e.Category)
            .Select(g => (Category: g.Key, Score: g.Sum(e => SeverityWeight.Of(e.Severity))))
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Category) // desempate estável
            .Take(TopCategories)
            .Select(c => c.Category)
            .ToHashSet();

        // Do mais recente pro mais antigo: o que o usuário erra agora aparece primeiro.
        var rows = inWindow
            .Where(e => top.Contains(e.Category))
            .OrderByDescending(e => e.CompletedAt)
            .ThenBy(e => e.Id)
            .ToList();

        return (windowIds.Count, rows);
    }

    /// <summary>Agregado do histórico inteiro — o mapa do todo, barato de mandar.</summary>
    private static IReadOnlyList<CategoryWeight> LifetimeByCategory(IReadOnlyList<ErrorRecord> errors) =>
        errors
            .GroupBy(e => e.Category)
            .Select(g => new CategoryWeight(g.Key, g.Count(), g.Sum(e => SeverityWeight.Of(e.Severity))))
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.Count)
            .ThenBy(c => c.Category)
            .ToList();

    // ── Conferência de evidência ─────────────────────────────────────────────

    /// <summary>
    /// Converte os padrões crus em padrões com lastro: mantém só os ids que estavam
    /// no material enviado, descarta o padrão que ficar abaixo de
    /// <see cref="MinEvidence"/>, e copia o erro pra dentro (snapshot).
    ///
    /// As categorias são <b>derivadas</b> da evidência, nunca pedidas ao modelo —
    /// uma superfície a menos pra ele errar.
    /// </summary>
    private static List<AnalysisPattern> Ground(
        IReadOnlyList<DraftPattern> drafts, IReadOnlyList<ErrorRecord> sent)
    {
        var allowed = sent.ToDictionary(e => e.Id);

        var grounded = new List<AnalysisPattern>();
        foreach (var draft in drafts)
        {
            var evidence = draft.EvidenceErrorIds
                .Distinct()
                .Where(allowed.ContainsKey)
                .Select(id => allowed[id])
                .Select(e => new AnalysisEvidence(
                    e.PracticeId, e.Category, e.Severity, e.Original, e.Correction, e.Explanation))
                .ToList();

            if (evidence.Count < MinEvidence) continue; // sem lastro suficiente → fora

            grounded.Add(new AnalysisPattern(
                draft.Title.Trim(),
                draft.Diagnosis.Trim(),
                evidence.Select(e => e.Category).Distinct().ToList(),
                evidence));

            if (grounded.Count == MaxPatterns) break;
        }

        return grounded;
    }

    private static List<AnalysisStudyItem> StudyItems(IReadOnlyList<AnalysisStudyItem> items) =>
        items
            .Where(i => !string.IsNullOrWhiteSpace(i.Title) && !string.IsNullOrWhiteSpace(i.Content))
            .Take(MaxStudyItems)
            .Select(i => new AnalysisStudyItem(i.Kind, i.Title.Trim(), i.Content.Trim()))
            .ToList();

    // ── Gate ─────────────────────────────────────────────────────────────────

    private static AnalysisGate Gate(
        int practices, int errors, AnalysisRecord? latest, int newSinceLatest)
    {
        if (practices < MinPractices || errors < MinErrors) return AnalysisGate.NotEnoughData;
        if (latest is not null && newSinceLatest < NewPracticesToRefresh) return AnalysisGate.UpToDate;
        return AnalysisGate.Ready;
    }

    // ── Carga / mapeamento ───────────────────────────────────────────────────

    /// <summary>
    /// Carrega práticas concluídas e seus erros. Materializa e filtra em memória pelo
    /// mesmo motivo do <see cref="PracticeService"/>: o SQLite não traduz bem GroupBy
    /// sobre enum-as-string nem ORDER BY sobre DateTimeOffset. Volume pessoal.
    /// </summary>
    private async Task<(List<CompletedPractice> Practices, List<ErrorRecord> Errors)> LoadCompletedAsync(
        CancellationToken ct)
    {
        var practices = (await _db.Exercises
            .Where(p => p.Status == PracticeStatus.Completed)
            .Select(p => new { p.Id, p.CompletedAt })
            .ToListAsync(ct))
            .Where(p => p.CompletedAt is not null)
            .Select(p => new CompletedPractice(p.Id, p.CompletedAt!.Value))
            .ToList();

        var completedAt = practices.ToDictionary(p => p.Id, p => p.CompletedAt);

        var errors = (await _db.Errors
            .Select(e => new
            {
                e.Id,
                e.ExerciseAttemptId,
                e.Category,
                e.Severity,
                e.Original,
                e.Correction,
                e.Explanation,
            })
            .ToListAsync(ct))
            .Where(e => completedAt.ContainsKey(e.ExerciseAttemptId))
            .Select(e => new ErrorRecord(
                e.Id, e.ExerciseAttemptId, completedAt[e.ExerciseAttemptId],
                e.Category, e.Severity, e.Original, e.Correction, e.Explanation))
            .ToList();

        return (practices, errors);
    }

    /// <summary>A análise mais recente. Ordena em memória (DateTimeOffset no SQLite, de novo).</summary>
    private async Task<AnalysisRecord?> LoadLatestAsync(CancellationToken ct) =>
        (await _db.Analyses.ToListAsync(ct))
        .OrderByDescending(a => a.GeneratedAt)
        .ThenByDescending(a => a.Id)
        .FirstOrDefault();

    private static WeaknessAnalysis ToContract(AnalysisRecord record) => new(
        record.Id,
        record.GeneratedAt,
        record.PracticesAnalyzed,
        record.ErrorsAnalyzed,
        JsonSerializer.Deserialize<List<AnalysisPattern>>(record.PatternsJson, Json) ?? [],
        JsonSerializer.Deserialize<List<AnalysisStudyItem>>(record.StudyItemsJson, Json) ?? []);

    private sealed record CompletedPractice(int Id, DateTimeOffset CompletedAt);

    private sealed record ErrorRecord(
        int Id,
        int PracticeId,
        DateTimeOffset CompletedAt,
        ErrorCategory Category,
        ErrorSeverity Severity,
        string Original,
        string Correction,
        string Explanation);
}
