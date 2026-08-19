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
    private const int PreviewLength = 120;
    private const int FocusCategoryCount = 3;

    /// <summary>Tamanho do recorte "recente" do perfil: as N práticas concluídas mais novas.</summary>
    private const int RecentWindow = 5;

    private readonly ILlmProvider _llm;
    private readonly WriteRightDbContext _db;
    private readonly UsageService _usage;
    private readonly CardService _cards;

    public PracticeService(ILlmProvider llm, WriteRightDbContext db, UsageService usage, CardService cards)
    {
        _llm = llm;
        _db = db;
        _usage = usage;
        _cards = cards;
    }

    /// <summary>
    /// Cria uma prática: gera o texto e persiste como <see cref="PracticeStatus.InProgress"/>.
    /// Se <see cref="CreatePracticeRequest.FocusOnWeaknesses"/>, mira a geração nas
    /// categorias mais frequentes do perfil (direcionamento adaptativo no servidor).
    /// </summary>
    public async Task<PracticeDetail> CreatePracticeAsync(CreatePracticeRequest request, CancellationToken ct = default)
    {
        if (request.SourceLanguage == request.TargetLanguage)
            throw new ArgumentException(
                "Origem e alvo devem ser idiomas diferentes.", nameof(request));

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

    /// <summary>Listagem da tela inicial: resumos leves, da mais recente pra mais antiga.</summary>
    public async Task<IReadOnlyList<PracticeSummary>> ListPracticesAsync(CancellationToken ct = default)
    {
        // Projeta os campos necessários (ErrorCount vira subquery COUNT). O preview
        // é truncado e a ordenação é feita EM MEMÓRIA: o SQLite não traduz nem
        // Substring nem ORDER BY sobre DateTimeOffset. (Volume pessoal — custo irrelevante.)
        var rows = await _db.Exercises
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

        return rows
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new PracticeSummary(
                r.Id, r.SourceLanguage, r.TargetLanguage, r.Level, r.Theme, r.Status,
                Preview(r.SourceText), r.ErrorCount, r.CreatedAt))
            .ToList();
    }

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
    /// </summary>
    public async Task<(PracticeOutcome Outcome, PracticeDetail? Detail)> CorrectPracticeAsync(
        int id, string userTranslation, CancellationToken ct = default)
    {
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
                    practice.SourceText, userTranslation, practice.Level, practice.Theme),
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
            // "sem correspondência" tem UMA representação (null): o schema pede string
            // vazia, o banco guarda null. Sem isto, "" e null significariam a mesma
            // coisa em colunas diferentes e todo consumidor teria que testar os dois.
            SourcePhrase = string.IsNullOrWhiteSpace(e.SourcePhrase) ? null : e.SourcePhrase.Trim(),
        }).ToList();
        practice.Status = PracticeStatus.Completed;
        practice.CompletedAt = DateTimeOffset.UtcNow;

        // Correção e consumo na mesma transação, com o token não cancelável: se o
        // navegador desistiu, a prática fica corrigida e o usuário a encontra ao
        // recarregar, em vez de pagar outra correção.
        await _db.SaveChangesAsync(UsageService.AfterBilling);

        // Cards num SaveChanges SEPARADO, e não junto do de cima: se a cunhagem
        // falhar dentro da mesma transação, a correção some com ela — e ela já foi
        // paga. Perder alguns cards (os erros continuam no perfil) é muito melhor
        // que fazer o usuário pagar outra correção. O deck é consequência da
        // correção, não condição dela.
        var minted = await _cards.MintForPracticeAsync(practice, UsageService.AfterBilling);
        await _db.SaveChangesAsync(UsageService.AfterBilling);

        return (PracticeOutcome.Ok, ToDetail(practice) with { MintedCards = minted });
    }

    /// <summary>Exclui uma prática (e seus erros, por cascade). Permitido em qualquer status.</summary>
    public async Task<PracticeOutcome> DeletePracticeAsync(int id, CancellationToken ct = default)
    {
        var practice = await _db.Exercises.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (practice is null) return PracticeOutcome.NotFound;

        _db.Exercises.Remove(practice);
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
    private static ProfileView BuildView(IReadOnlyList<ErrorRow> errors, int attempts)
    {
        var byCategory = errors
            .GroupBy(e => e.Category)
            .Select(g => new CategoryWeight(g.Key, g.Count(), g.Sum(e => SeverityWeight.Of(e.Severity))))
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.Count)
            .ThenBy(c => c.Category) // desempate estável (ordenação determinística)
            .ToList();

        return new ProfileView(
            attempts, byCategory.Sum(c => c.Count), byCategory.Sum(c => c.Score), byCategory);
    }

    /// <summary>Linha de erro materializada do banco, pra agregar em memória.</summary>
    private sealed record ErrorRow(ErrorCategory Category, ErrorSeverity Severity, int AttemptId);

    private static string Preview(string sourceText) =>
        sourceText.Length <= PreviewLength ? sourceText : sourceText[..PreviewLength];

    private static PracticeDetail ToDetail(ExerciseAttempt p)
    {
        var completed = p.Status == PracticeStatus.Completed;
        return new PracticeDetail(
            p.Id, p.SourceLanguage, p.TargetLanguage, p.Level, p.Theme, p.Status,
            p.SourceText, p.UserTranslation,
            completed ? p.CorrectedText : null,
            p.Errors.Select(e => new WritingError(
                e.Category, e.Severity, e.Original, e.Correction, e.Explanation, e.SourcePhrase)).ToList(),
            p.CreatedAt, p.CompletedAt);
    }
}
