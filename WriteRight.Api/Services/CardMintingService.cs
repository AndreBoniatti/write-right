using Microsoft.EntityFrameworkCore;
using WriteRight.Api.Data;
using WriteRight.Api.Llm;
using WriteRight.Shared.Cards;
using WriteRight.Shared.Usage;

namespace WriteRight.Api.Services;

/// <summary>
/// O passo que transforma uma prática corrigida em cards: pergunta ao modelo o que
/// vira card, registra o gasto e entrega as propostas ao <see cref="CardService"/>.
///
/// Roda FORA da requisição de correção (quem chama é o <see cref="CardWorker"/>), e a
/// fila é o próprio banco: a correção grava <see cref="CardsStatus.Pending"/> na mesma
/// transação, e este serviço só lê dali. Por isso um restart no meio não perde nada.
/// </summary>
public sealed class CardMintingService
{
    private readonly WriteRightDbContext _db;
    private readonly ILlmProvider _llm;
    private readonly UsageService _usage;
    private readonly CardService _cards;

    public CardMintingService(
        WriteRightDbContext db, ILlmProvider llm, UsageService usage, CardService cards)
    {
        _db = db;
        _llm = llm;
        _usage = usage;
        _cards = cards;
    }

    /// <summary>Práticas esperando cards, da mais antiga pra mais nova.</summary>
    public async Task<IReadOnlyList<int>> PendingAsync(CancellationToken ct = default) =>
        await _db.Exercises
            .Where(p => p.CardsStatus == CardsStatus.Pending)
            .OrderBy(p => p.Id)
            .Select(p => p.Id)
            .ToListAsync(ct);

    /// <summary>
    /// Cunha os cards de uma prática pendente. Sem efeito se ela não estiver mais
    /// pendente (apagada, ou já processada por outra passada).
    ///
    /// Falha da IA SOBE como exceção — quem decide tentar de novo ou desistir é o
    /// worker. O gasto, esse, é gravado antes de subir: dinheiro gasto é fato
    /// consumado, independente do desfecho.
    /// </summary>
    public async Task<int> MintAsync(int practiceId)
    {
        // Nada aqui é cancelável, pelo mesmo motivo do AnalysisWorker: abortar no meio
        // de uma chamada já em curso não devolve o dinheiro, só esconde o gasto.
        var ct = UsageService.AfterBilling;

        var practice = await _db.Exercises
            .Include(p => p.Errors)
            .FirstOrDefaultAsync(p => p.Id == practiceId, ct);

        if (practice is not { CardsStatus: CardsStatus.Pending }) return 0;

        // A ORDEM é contrato: o modelo cita o erro pela posição nesta lista, e o
        // portão a lê na mesma ordem. Id crescente = a ordem em que a correção os devolveu.
        var errors = practice.Errors.OrderBy(e => e.Id).ToList();

        // Prática sem erro não tem o que virar card — e não vale uma chamada.
        if (errors.Count == 0)
        {
            practice.CardsStatus = CardsStatus.Done;
            await _db.SaveChangesAsync(ct);
            return 0;
        }

        LlmResult<CardDesign> result;
        try
        {
            result = await _llm.DesignCardsAsync(
                new CardDesignRequest(
                    practice.SourceLanguage, practice.TargetLanguage,
                    practice.SourceText, practice.UserTranslation, practice.CorrectedText,
                    errors.Select(e => new CardDesignError(e.Original, e.Correction)).ToList()),
                ct);
        }
        catch (LlmCallFailedException ex)
        {
            _usage.Record(LlmOperation.Cards, ex.Usage, practiceId: practice.Id);
            await _db.SaveChangesAsync(ct);
            throw;
        }

        // O gasto num SaveChanges próprio, ANTES de cunhar: se a cunhagem quebrar, a
        // prática fica pendente e volta pra fila — mas o registro do que já foi pago
        // não pode ir embora junto.
        _usage.Record(LlmOperation.Cards, result.Usage, practiceId: practice.Id);
        await _db.SaveChangesAsync(ct);

        var minted = await _cards.MintAsync(practice, errors, result.Value.Decisions, ct);
        practice.CardsStatus = CardsStatus.Done;
        await _db.SaveChangesAsync(ct);

        return minted;
    }

    /// <summary>Desiste de uma prática depois de esgotadas as tentativas.</summary>
    public async Task MarkFailedAsync(int practiceId)
    {
        var practice = await _db.Exercises.FirstOrDefaultAsync(p => p.Id == practiceId);
        if (practice is not { CardsStatus: CardsStatus.Pending }) return;

        practice.CardsStatus = CardsStatus.Failed;
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Devolve as práticas que falharam pra fila. Falha nunca volta sozinha (nem no
    /// boot): cada tentativa é uma chamada paga, e um erro que se repete viraria gasto
    /// em loop. Só por pedido explícito.
    /// </summary>
    public async Task<int> RetryFailedAsync(CancellationToken ct = default)
    {
        var failed = await _db.Exercises
            .Where(p => p.CardsStatus == CardsStatus.Failed)
            .ToListAsync(ct);

        foreach (var practice in failed) practice.CardsStatus = CardsStatus.Pending;
        await _db.SaveChangesAsync(ct);
        return failed.Count;
    }
}
