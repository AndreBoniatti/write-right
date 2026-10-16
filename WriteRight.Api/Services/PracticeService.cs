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
/// Orquestra o ciclo de vida da prática: cria (gera + persiste como InProgress),
/// lista, retoma, salva rascunho, corrige (→ Completed, readonly), exclui, e monta
/// o perfil de fraquezas. Mantém o <see cref="ILlmProvider"/> focado só na IA.
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

    /// <summary>Tamanho do recorte "recente" do perfil: as N práticas concluídas mais novas.</summary>
    private const int RecentWindow = 5;

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
    /// categorias mais frequentes do perfil (direcionamento adaptativo no servidor).
    /// </summary>
    public async Task<PracticeDetail> CreatePracticeAsync(CreatePracticeRequest request, CancellationToken ct = default)
    {
        if (request.SourceLanguage == request.TargetLanguage)
            throw new ArgumentException("Origem e alvo devem ser idiomas diferentes.");

        if (request.WordCount is < PracticeLimits.MinWords or > PracticeLimits.MaxWords)
            throw new ArgumentException(
                $"O texto deve ter entre {PracticeLimits.MinWords} e {PracticeLimits.MaxWords} palavras.");

        IReadOnlyList<ErrorCategory>? focus = null;
        if (request.FocusOnWeaknesses)
        {
            // Mira o top-N vitalício — já ordenado por PESO (gravidade × frequência),
            // não por contagem crua. Logo a geração ataca o que mais atrapalha.
            var profile = await GetProfileAsync(ct);
            if (profile.Lifetime.ByCategory.Count > 0)
                focus = profile.Lifetime.ByCategory.Take(FocusCategoryCount).Select(c => c.Category).ToList();
        }

        // Sorteia a FORMA do texto (tempo verbal, registro, ponto de vista, assunto).
        // Sem isto, uma prática sem foco manda um prompt idêntico toda vez — e prompt
        // idêntico devolve sempre o mesmo texto modal. A variedade tem que vir daqui,
        // porque o modelo não lembra o que já gerou.
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

        // Token não cancelável daqui pra frente: o texto já foi pago. Descartá-lo
        // porque o navegador desistiu significaria pagar de novo pelo mesmo texto.
        _db.Exercises.Add(practice);
        await _db.SaveChangesAsync(UsageService.AfterBilling);

        // Segundo save: o Id da prática só existe depois do primeiro. Duas idas ao
        // banco logo após uma chamada de rede de vários segundos — irrelevante.
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
        // Ordena pelo Id, não pelo CreatedAt: o SQLite não faz ORDER BY sobre
        // DateTimeOffset, e a ordem tem que estar no SQL pra a página sair do banco.
        // O Id cresce junto com a data — os dois nascem no mesmo insert.
        // ErrorCount vira subquery COUNT; o preview é cortado em memória.
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
    /// praticou. Sem isso, apagar a única prática de cinco dias atrás quebrava a série.
    /// Só a prática de tradução conta — revisar cards não acende a chama, de propósito:
    /// o incentivo é fazer uma prática de verdade.
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
    /// Tradução em branco é recusada antes de tudo: corrigir nada ainda seria cobrado.
    /// </summary>
    public async Task<(PracticeOutcome Outcome, PracticeDetail? Detail)> CorrectPracticeAsync(
        int id, string userTranslation, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userTranslation))
            throw new ArgumentException("A tradução está em branco.");

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
            // A correção mais cara é a que estoura o teto de saída e não desserializa.
            // A prática segue em andamento (dá pra tentar de novo), mas o gasto fica.
            _usage.Record(LlmOperation.Correction, ex.Usage, practiceId: practice.Id);
            await _db.SaveChangesAsync(UsageService.AfterBilling);
            throw;
        }

        // A prática já tem Id aqui, então o registro entra na MESMA transação da correção.
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
        // rodar, o próximo boot retoma. O deck é consequência da correção, não
        // condição dela.
        practice.CardsStatus = CardsStatus.Pending;

        // Correção e consumo na mesma transação, com o token não cancelável: se o
        // navegador desistiu, a prática fica corrigida e o usuário a encontra ao
        // recarregar, em vez de pagar outra correção.
        await _db.SaveChangesAsync(UsageService.AfterBilling);
        _cardJobs.Notify();

        return (PracticeOutcome.Ok, ToDetail(practice));
    }

    /// <summary>
    /// Exclui uma prática, em qualquer status. Exclusão LÓGICA: carimba
    /// <see cref="ExerciseAttempt.DeletedAt"/> e o filtro global a tira — com os erros
    /// junto — da lista, do detalhe, do perfil e das próximas análises. A linha fica
    /// porque carrega fatos que excluir não desfaz: o dia em que você praticou e o que
    /// custou. Excluir de novo devolve NotFound: pro filtro, ela já não existe.
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
    /// Perfil de fraquezas, ponderado por severidade (Quebra o sentido ×3,
    /// Compreensível ×2, Lapidação ×1) — o que atrapalha pesa mais que a lapidação.
    /// Devolve duas visões: <c>Lifetime</c> (todo o histórico, insumo do gerador
    /// adaptativo) e <c>Recent</c> (as últimas <see cref="RecentWindow"/> práticas
    /// concluídas, onde o usuário vaza agora). Só práticas CONCLUÍDAS entram (as em
    /// andamento ainda não têm erros, por construção).
    /// </summary>
    public async Task<ErrorProfile> GetProfileAsync(CancellationToken ct = default)
    {
        // Agrega em memória (dados pessoais, volume baixo) — evita depender da
        // tradução de GroupBy sobre enum-as-string e de ORDER BY sobre
        // DateTimeOffset no SQLite (mesmo motivo de ListPracticesAsync).
        var completed = await _db.Exercises
            .Where(p => p.Status == PracticeStatus.Completed)
            .Select(p => new { p.Id, p.CompletedAt })
            .ToListAsync(ct);
        var completedIds = completed.Select(a => a.Id).ToHashSet();

        // Todo erro pertence a uma prática concluída (só a correção grava erros); o
        // filtro por completedIds é rede de segurança, não corte esperado.
        var errors = (await _db.Errors
            .Select(e => new { e.Category, e.Severity, e.ExerciseAttemptId })
            .ToListAsync(ct))
            .Where(e => completedIds.Contains(e.ExerciseAttemptId))
            .Select(e => new ErrorRow(e.Category, e.Severity, e.ExerciseAttemptId))
            .ToList();

        var recentIds = completed
            .OrderByDescending(a => a.CompletedAt)
            .Take(RecentWindow)
            .Select(a => a.Id)
            .ToHashSet();

        var lifetime = BuildView(errors, completed.Count);
        var recent = BuildView(
            errors.Where(e => recentIds.Contains(e.AttemptId)).ToList(), recentIds.Count);

        return new ErrorProfile(lifetime, recent);
    }

    /// <summary>
    /// Os erros reais do usuário numa categoria — material da tela de revisão
    /// ("meus erros de Preposição"), do mais recente pro mais antigo. Só de práticas
    /// concluídas. Releitura pura do histórico: NÃO chama a IA.
    /// </summary>
    public async Task<IReadOnlyList<CategoryError>> GetCategoryErrorsAsync(
        ErrorCategory category, CancellationToken ct = default)
    {
        // Filtra por categoria no SQL (há índice em Category); status e ordem por
        // data resolvem-se em memória (o SQLite não ordena bem DateTimeOffset — mesmo
        // motivo de ListPracticesAsync).
        var rows = await _db.Errors
            .Where(e => e.Category == category)
            .Select(e => new
            {
                e.Severity,
                e.Original,
                e.Correction,
                e.Explanation,
                e.ExerciseAttemptId,
                e.ExerciseAttempt!.Status,
                e.ExerciseAttempt!.CompletedAt,
            })
            .ToListAsync(ct);

        return rows
            .Where(r => r.Status == PracticeStatus.Completed && r.CompletedAt is not null)
            .OrderByDescending(r => r.CompletedAt)
            .Select(r => new CategoryError(
                r.ExerciseAttemptId, r.CompletedAt!.Value, r.Severity,
                r.Original, r.Correction, r.Explanation))
            .ToList();
    }

    /// <summary>Monta uma visão do perfil: agrupa por categoria, pondera por severidade e ordena por peso.</summary>
    private static ProfileView BuildView(IReadOnlyList<ErrorRow> errors, int attempts) =>
        new(attempts, errors.Count, CategoryRanking.Rank(errors.Select(e => (e.Category, e.Severity))));

    /// <summary>Linha de erro materializada do banco, pra agregar em memória.</summary>
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
