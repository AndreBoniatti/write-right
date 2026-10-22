using Microsoft.EntityFrameworkCore;
using WriteRight.Api.Data;
using WriteRight.Api.Llm;
using WriteRight.Shared.Corrections;
using WriteRight.Shared.Exercises;
using WriteRight.Shared.Practices;
using WriteRight.Shared.Profile;
using WriteRight.Shared.Taxonomy;
using WriteRight.Shared.Usage;

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
/// Orquestra o ciclo de vida da prática (criar → salvar rascunho → corrigir → excluir)
/// e monta o perfil de fraquezas. Mantém o <see cref="ILlmProvider"/> focado só na IA.
/// </summary>
public sealed class PracticeService
{
    /// <summary>Práticas por página da listagem — quem chama não escolhe, só pede a próxima.</summary>
    public const int PageSize = 20;

    /// <summary>
    /// Longo o bastante pra encher uma linha de tela larga: quem corta no que cabe é
    /// o CSS, com reticências. Se o corte daqui aparecesse antes, a linha acabaria
    /// no meio de uma palavra.
    /// </summary>
    private const int PreviewLength = 160;
    private const int FocusCategoryCount = 3;

    /// <summary>
    /// Tamanho do recorte "recente" do perfil. É a mesma janela do gráfico de evolução:
    /// a tela diz "últimas N" nos dois lugares, e duas constantes acabariam dizendo
    /// números diferentes.
    /// </summary>
    public const int RecentWindow = ErrorRate.Window;

    private readonly ILlmProvider _llm;
    private readonly WriteRightDbContext _db;
    private readonly UsageService _usage;
    private readonly CardJobSignal _cardJobs;

    public PracticeService(ILlmProvider llm, WriteRightDbContext db, UsageService usage, CardJobSignal cardJobs)
    {
        _llm = llm;
        _db = db;
        _usage = usage;
        _cardJobs = cardJobs;
    }

    /// <summary>
    /// Cria uma prática: gera o texto e persiste como <see cref="PracticeStatus.InProgress"/>.
    /// Se <see cref="CreatePracticeRequest.FocusOnWeaknesses"/>, mira a geração nas
    /// categorias mais pesadas do perfil.
    /// </summary>
    public async Task<PracticeDetail> CreatePracticeAsync(CreatePracticeRequest request, CancellationToken ct = default)
    {
        if (request.SourceLanguage == request.TargetLanguage)
            throw new InvalidRequestException("Origem e alvo devem ser idiomas diferentes.");

        if (request.WordCount is < PracticeLimits.MinWords or > PracticeLimits.MaxWords)
            throw new InvalidRequestException(
                $"O texto deve ter entre {PracticeLimits.MinWords} e {PracticeLimits.MaxWords} palavras.");

        IReadOnlyList<ErrorCategory>? focus = null;
        if (request.FocusOnWeaknesses)
        {
            // Top-N vitalício por PESO (gravidade × frequência), não por contagem crua.
            // Só o ranking: o perfil inteiro leria todas as traduções pra montar o gráfico.
            var ranking = CategoryRanking.Rank(
                (await CompletedErrorsAsync(ct)).Select(e => (e.Category, e.Severity)));
            if (ranking.Count > 0)
                focus = ranking.Take(FocusCategoryCount).Select(c => c.Category).ToList();
        }

        // Sorteia a FORMA do texto (tempo verbal, registro, ponto de vista, assunto).
        // Prompt idêntico devolve sempre o mesmo texto modal, e o modelo não lembra o
        // que já gerou — a variedade tem que vir daqui.
        LlmResult<GeneratedExercise> generated;
        try
        {
            generated = await _llm.GenerateExerciseAsync(
                new ExerciseGenerationRequest(
                    request.SourceLanguage, request.TargetLanguage, request.WordCount,
                    request.Level, request.Theme, focus, VarietyCatalog.Pick()),
                ct);
        }
        catch (LlmCallFailedException ex)
        {
            // Cobrado sem produzir texto: não há prática pra vincular, mas o gasto
            // existe e some do registro se não for gravado aqui.
            _usage.Record(LlmOperation.Generation, ex.Usage);
            await _db.SaveChangesAsync(UsageService.AfterBilling);
            throw;
        }

        var practice = new ExerciseAttempt
        {
            Status = PracticeStatus.InProgress,
            SourceLanguage = request.SourceLanguage,
            TargetLanguage = request.TargetLanguage,
            Level = request.Level,
            Theme = request.Theme,
            SourceText = generated.Value.SourceText,
            UserTranslation = "",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _db.Exercises.Add(practice);
        await _db.SaveChangesAsync(UsageService.AfterBilling);

        // Segundo save: o Id da prática só existe depois do primeiro.
        _usage.Record(LlmOperation.Generation, generated.Usage, practiceId: practice.Id);
        await _db.SaveChangesAsync(UsageService.AfterBilling);

        return ToDetail(practice);
    }

    /// <summary>
    /// Uma página da listagem da tela inicial: as em andamento primeiro (são as que
    /// pedem ação, e não podem ficar enterradas três páginas abaixo), depois da mais
    /// recente pra mais antiga.
    /// </summary>
    public async Task<PracticePage> ListPracticesAsync(
        int skip = 0, int take = PageSize, CancellationToken ct = default)
    {
        // Pelo Id: a mesma ordem da criação, e única — a paginação precisa de ordem
        // total, ou uma prática aparece em duas páginas e outra em nenhuma.
        var rows = await _db.Exercises
            .OrderByDescending(p => p.Status == PracticeStatus.InProgress)
            .ThenByDescending(p => p.Id)
            .Skip(Math.Max(0, skip))
            .Take(take)
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

        var total = await _db.Exercises.CountAsync(ct);

        var items = rows
            .Select(r => new PracticeSummary(
                r.Id, r.SourceLanguage, r.TargetLanguage, r.Level, r.Theme, r.Status,
                Preview(r.SourceText), r.ErrorCount, r.CreatedAt))
            .ToList();

        return new PracticePage(items, total);
    }

    /// <summary>
    /// Quando cada prática concluída foi concluída — a matéria-prima da série de dias.
    /// Instantes crus, não dias: o dia depende do fuso de quem está vendo, e quem sabe
    /// o fuso é o cliente (ver <see cref="PracticeStreak"/>).
    ///
    /// Inclui as excluídas: excluir é arrumar a lista, não desfazer o dia em que você
    /// praticou. Só a prática de tradução conta — revisar cards não acende a chama, de
    /// propósito: o incentivo é fazer uma prática de verdade.
    /// </summary>
    public async Task<IReadOnlyList<DateTimeOffset>> ListCompletionTimesAsync(CancellationToken ct = default) =>
        await _db.Exercises
            .IgnoreQueryFilters([WriteRightDbContext.SoftDeleteFilter])
            .Where(p => p.Status == PracticeStatus.Completed && p.CompletedAt != null)
            .Select(p => p.CompletedAt!.Value)
            .ToListAsync(ct);

    /// <summary>Detalhe completo de uma prática (retomar ou ler). Null se não existe.</summary>
    public async Task<PracticeDetail?> GetPracticeAsync(int id, CancellationToken ct = default)
    {
        var practice = await _db.Exercises
            .Include(p => p.Errors)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        return practice is null ? null : ToDetail(practice);
    }

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
    /// como <see cref="PracticeStatus.Completed"/> (readonly). Uma prática já concluída
    /// volta <see cref="PracticeOutcome.ReadOnly"/>. Tradução em branco é recusada antes
    /// de tudo: corrigir nada ainda seria cobrado.
    /// </summary>
    public async Task<(PracticeOutcome Outcome, PracticeDetail? Detail)> CorrectPracticeAsync(
        int id, string userTranslation, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userTranslation))
            throw new InvalidRequestException("A tradução está em branco.");

        var practice = await _db.Exercises
            .Include(p => p.Errors)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (practice is null) return (PracticeOutcome.NotFound, null);
        if (practice.Status == PracticeStatus.Completed) return (PracticeOutcome.ReadOnly, null);

        LlmResult<CorrectionResult> result;
        try
        {
            result = await _llm.CorrectAsync(
                new CorrectionRequest(
                    practice.SourceLanguage, practice.TargetLanguage,
                    practice.SourceText, userTranslation),
                ct);
        }
        catch (LlmCallFailedException ex)
        {
            // A prática segue em andamento (dá pra tentar de novo), mas o gasto fica.
            _usage.Record(LlmOperation.Correction, ex.Usage, practiceId: practice.Id);
            await _db.SaveChangesAsync(UsageService.AfterBilling);
            throw;
        }

        _usage.Record(LlmOperation.Correction, result.Usage, practiceId: practice.Id);

        var correction = result.Value;
        practice.UserTranslation = userTranslation;
        practice.CorrectedText = correction.CorrectedText;
        practice.Errors = correction.Errors.Select(e => new ExerciseError
        {
            Category = e.Category,
            Severity = e.Severity,
            Original = e.Original,
            Correction = e.Correction,
            Explanation = e.Explanation,
        }).ToList();
        practice.Status = PracticeStatus.Completed;
        practice.CompletedAt = DateTimeOffset.UtcNow;

        // Os cards ficam pra depois, fora desta requisição (ver CardWorker) — mas a
        // PROMESSA deles entra aqui, na mesma transação da correção. Assim não existe
        // prática corrigida sem cards pendentes: se o app cair antes de o worker
        // rodar, o próximo boot retoma.
        practice.CardsStatus = CardsStatus.Pending;

        // Correção, consumo e promessa dos cards numa transação só.
        await _db.SaveChangesAsync(UsageService.AfterBilling);
        _cardJobs.Notify();

        return (PracticeOutcome.Ok, ToDetail(practice));
    }

    /// <summary>
    /// Exclui uma prática, em qualquer status. Exclusão LÓGICA: carimba
    /// <see cref="ExerciseAttempt.DeletedAt"/> e o filtro global a tira — com os erros
    /// junto — de toda consulta. A linha fica porque carrega fatos que excluir não
    /// desfaz: o dia em que você praticou e o que custou.
    /// </summary>
    public async Task<PracticeOutcome> DeletePracticeAsync(int id, CancellationToken ct = default)
    {
        var practice = await _db.Exercises.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (practice is null) return PracticeOutcome.NotFound;

        practice.DeletedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return PracticeOutcome.Ok;
    }

    /// <summary>
    /// Perfil de fraquezas, ponderado por severidade (ver <see cref="SeverityWeight"/>).
    /// Duas visões: <c>Lifetime</c> (todo o histórico, insumo do gerador adaptativo) e
    /// <c>Recent</c> (as últimas <see cref="RecentWindow"/> concluídas), mais a linha do
    /// tempo por prática do gráfico de evolução.
    /// </summary>
    public async Task<ErrorProfile> GetProfileAsync(CancellationToken ct = default)
    {
        // A tradução vem inteira só pra contar as palavras; o texto não sai daqui.
        var completed = await _db.Exercises
            .Where(p => p.Status == PracticeStatus.Completed && p.CompletedAt != null)
            .OrderBy(p => p.CompletedAt)
            .ThenBy(p => p.Id)
            .Select(p => new { p.Id, p.CompletedAt, p.Level, p.UserTranslation })
            .ToListAsync(ct);

        // Só os erros das práticas lidas acima: uma correção que termine entre as duas
        // leituras entraria no total de erros sem entrar nas práticas nem na linha do tempo.
        var completedIds = completed.Select(p => p.Id).ToHashSet();
        var errors = (await CompletedErrorsAsync(ct)).Where(e => completedIds.Contains(e.AttemptId)).ToList();

        var recentIds = completed
            .TakeLast(RecentWindow)
            .Select(a => a.Id)
            .ToHashSet();

        var lifetime = BuildView(errors, completed.Count);
        var recent = BuildView(
            errors.Where(e => recentIds.Contains(e.AttemptId)).ToList(), recentIds.Count);

        var severitiesByAttempt = errors.ToLookup(e => e.AttemptId, e => e.Severity);
        var timeline = completed
            .Select(p => new PracticePoint(
                p.Id, p.CompletedAt!.Value, p.Level,
                ErrorRate.Words(p.UserTranslation),
                SeverityCounts.Of(severitiesByAttempt[p.Id])))
            .ToList();

        return new ErrorProfile(lifetime, recent, timeline);
    }

    /// <summary>
    /// Os erros reais do usuário numa categoria — material da tela de revisão
    /// ("meus erros de Preposição"), do mais recente pro mais antigo. Só de práticas
    /// concluídas. Releitura pura do histórico: NÃO chama a IA.
    /// </summary>
    public async Task<IReadOnlyList<CategoryError>> GetCategoryErrorsAsync(
        ErrorCategory category, CancellationToken ct = default) =>
        await _db.Errors
            .Where(e => e.Category == category
                && e.ExerciseAttempt!.Status == PracticeStatus.Completed
                && e.ExerciseAttempt.CompletedAt != null)
            .OrderByDescending(e => e.ExerciseAttempt!.CompletedAt)
            .ThenBy(e => e.Id)
            .Select(e => new CategoryError(
                e.ExerciseAttemptId, e.ExerciseAttempt!.CompletedAt!.Value, e.Severity,
                e.Original, e.Correction, e.Explanation))
            .ToListAsync(ct);

    /// <summary>
    /// Os erros das práticas concluídas — a base do ranking da geração e do perfil.
    /// Todo erro já pertence a uma prática concluída (só a correção grava erros); o
    /// filtro é rede de segurança, não corte esperado.
    /// </summary>
    private async Task<List<ErrorRow>> CompletedErrorsAsync(CancellationToken ct) =>
        await _db.Errors
            .Where(e => e.ExerciseAttempt!.Status == PracticeStatus.Completed && e.ExerciseAttempt.CompletedAt != null)
            .Select(e => new ErrorRow(e.Category, e.Severity, e.ExerciseAttemptId))
            .ToListAsync(ct);

    private static ProfileView BuildView(IReadOnlyList<ErrorRow> errors, int attempts) =>
        new(attempts, errors.Count, CategoryRanking.Rank(errors.Select(e => (e.Category, e.Severity))));

    private sealed record ErrorRow(ErrorCategory Category, ErrorSeverity Severity, int AttemptId);

    private static string Preview(string sourceText) =>
        sourceText.Length <= PreviewLength ? sourceText : sourceText[..PreviewLength] + "…";

    private static PracticeDetail ToDetail(ExerciseAttempt p)
    {
        var completed = p.Status == PracticeStatus.Completed;
        return new PracticeDetail(
            p.Id, p.SourceLanguage, p.TargetLanguage, p.Level, p.Theme, p.Status,
            p.SourceText, p.UserTranslation,
            completed ? p.CorrectedText : null,
            p.Errors.Select(e => new WritingError(
                e.Category, e.Severity, e.Original, e.Correction, e.Explanation)).ToList(),
            p.CreatedAt, p.CompletedAt);
    }
}
