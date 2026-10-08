using Microsoft.EntityFrameworkCore;
using WriteRight.Api.Data;
using WriteRight.Api.Llm;
using WriteRight.Api.Services;
using WriteRight.Shared;
using WriteRight.Shared.Cards;
using WriteRight.Shared.Practices;
using WriteRight.Shared.Taxonomy;
using WriteRight.Shared.Usage;
using WriteRight.Tests.Support;

namespace WriteRight.Tests.Cards;

/// <summary>
/// O passo que transforma uma prática corrigida em cards, fora da requisição. O que
/// se trava aqui é o ciclo do <see cref="CardsStatus"/> — é ele que impede a falha
/// invisível ("deviam ter nascido três cards e ninguém soube") — e que o gasto é
/// registrado mesmo quando dá errado.
/// </summary>
public sealed class CardMintingServiceTests : IDisposable
{
    private readonly TestDatabase _db = new();

    private CardMintingService Service(StubLlmProvider stub, out WriteRightDbContext ctx)
    {
        ctx = _db.NewContext();
        return new CardMintingService(
            ctx, stub, new UsageService(ctx, TestPricing.Default()), new CardService(ctx));
    }

    private async Task<int> SeedAsync(CardsStatus? status, params (string Original, string Correction)[] errors)
    {
        await using var ctx = _db.NewContext();
        var practice = new ExerciseAttempt
        {
            Status = PracticeStatus.Completed,
            SourceLanguage = Language.Portuguese,
            TargetLanguage = Language.English,
            SourceText = "A arquitetura apresenta características únicas e fachadas coloridas.",
            UserTranslation = "The architecture have unique aspects and colored fronts.",
            CorrectedText = "The architecture has unique characteristics and colorful façades.",
            CompletedAt = DateTimeOffset.UtcNow,
            CardsStatus = status,
            Errors = errors.Select(e => new ExerciseError
            {
                Category = ErrorCategory.WordChoice,
                Severity = ErrorSeverity.Understandable,
                Original = e.Original,
                Correction = e.Correction,
                Explanation = "porquê",
            }).ToList(),
        };
        ctx.Exercises.Add(practice);
        await ctx.SaveChangesAsync();
        return practice.Id;
    }

    private static readonly (string, string)[] TwoErrors =
    [
        ("colored fronts", "colorful façades"),
        ("have", "has"),
    ];

    private static CardDesign Design(params CardProposal[] decisions) => new(decisions);

    [Fact]
    public async Task Mints_the_approved_cards_and_marks_the_practice_done()
    {
        var id = await SeedAsync(CardsStatus.Pending, TwoErrors);
        var stub = new StubLlmProvider
        {
            CardDesign = Design(
                new CardProposal(1, "fachada = façade", CardDecisionKind.Card, "façades", "fachadas", []),
                new CardProposal(2, "concordância", CardDecisionKind.Grammar, "", "", [])),
        };

        var minted = await Service(stub, out var ctx).MintAsync(id);
        await ctx.DisposeAsync();

        Assert.Equal(1, minted);
        await using var check = _db.NewContext();
        var card = await check.Cards.SingleAsync();
        Assert.Equal("façades", card.Answer);
        Assert.Equal("colored fronts", card.YourAttempt);
        Assert.Equal(CardsStatus.Done, (await check.Exercises.SingleAsync()).CardsStatus);
    }

    /// <summary>
    /// O gasto entra no custo da PRÁTICA, com a operação própria — senão o relatório
    /// de uso esconderia quanto o deck custa.
    /// </summary>
    [Fact]
    public async Task Records_the_spend_against_the_practice()
    {
        var id = await SeedAsync(CardsStatus.Pending, TwoErrors);
        var stub = new StubLlmProvider { CardDesign = Design() };

        await Service(stub, out var ctx).MintAsync(id);
        await ctx.DisposeAsync();

        await using var check = _db.NewContext();
        var call = await check.LlmCalls.SingleAsync();
        Assert.Equal(LlmOperation.Cards, call.Operation);
        Assert.Equal(id, call.PracticeId);
        Assert.True(call.CostUsd > 0);
    }

    /// <summary>
    /// O modelo cita o erro pela POSIÇÃO — então a lista vai na ordem em que os erros
    /// foram gravados, e sem a categoria, que é o rótulo que errava ao decidir card.
    /// </summary>
    [Fact]
    public async Task Sends_the_texts_and_the_errors_in_order()
    {
        var id = await SeedAsync(CardsStatus.Pending, TwoErrors);
        var stub = new StubLlmProvider { CardDesign = Design() };

        await Service(stub, out var ctx).MintAsync(id);
        await ctx.DisposeAsync();

        var request = stub.LastCardRequest!;
        Assert.Equal("The architecture has unique characteristics and colorful façades.", request.CorrectedText);
        Assert.Equal("The architecture have unique aspects and colored fronts.", request.UserTranslation);
        Assert.Equal(
            [new CardDesignError("colored fronts", "colorful façades"), new CardDesignError("have", "has")],
            request.Errors);
    }

    /// <summary>Prática sem erro não tem o que virar card — e não vale uma chamada paga.</summary>
    [Fact]
    public async Task A_practice_without_errors_is_done_without_calling_the_model()
    {
        var id = await SeedAsync(CardsStatus.Pending);
        var stub = new StubLlmProvider();

        await Service(stub, out var ctx).MintAsync(id);
        await ctx.DisposeAsync();

        Assert.Equal(0, stub.CardCalls);
        await using var check = _db.NewContext();
        Assert.Equal(CardsStatus.Done, (await check.Exercises.SingleAsync()).CardsStatus);
        Assert.Empty(check.LlmCalls);
    }

    /// <summary>
    /// Só prática PENDENTE é processada. Feita, falhada ou sem status (prática antiga,
    /// ou em andamento) não chama o modelo — cada chamada a mais é dinheiro.
    /// </summary>
    [Theory]
    [InlineData(CardsStatus.Done)]
    [InlineData(CardsStatus.Failed)]
    [InlineData(null)]
    public async Task Does_nothing_for_a_practice_that_is_not_pending(CardsStatus? status)
    {
        var id = await SeedAsync(status, TwoErrors);
        var stub = new StubLlmProvider { CardDesign = Design() };

        await Service(stub, out var ctx).MintAsync(id);
        await ctx.DisposeAsync();

        Assert.Equal(0, stub.CardCalls);
    }

    /// <summary>Prática apagada enquanto esperava: nada a fazer, e nada pra quebrar.</summary>
    [Fact]
    public async Task Does_nothing_for_a_deleted_practice()
    {
        var stub = new StubLlmProvider { CardDesign = Design() };

        Assert.Equal(0, await Service(stub, out var ctx).MintAsync(999));
        await ctx.DisposeAsync();
        Assert.Equal(0, stub.CardCalls);
    }

    /// <summary>
    /// A chamada foi cobrada e não virou resultado: o gasto fica gravado, a falha
    /// SOBE (quem decide tentar de novo é o worker) e a prática continua pendente.
    /// </summary>
    [Fact]
    public async Task A_failed_call_records_the_spend_and_keeps_the_practice_pending()
    {
        var id = await SeedAsync(CardsStatus.Pending, TwoErrors);
        var stub = new StubLlmProvider { FailAfterBilling = true };

        var service = Service(stub, out var ctx);
        await Assert.ThrowsAsync<LlmCallFailedException>(() => service.MintAsync(id));
        await ctx.DisposeAsync();

        await using var check = _db.NewContext();
        Assert.Equal(LlmOperation.Cards, (await check.LlmCalls.SingleAsync()).Operation);
        Assert.Equal(CardsStatus.Pending, (await check.Exercises.SingleAsync()).CardsStatus);
        Assert.Empty(check.Cards);
    }

    [Fact]
    public async Task Pending_lists_only_pending_practices_oldest_first()
    {
        var first = await SeedAsync(CardsStatus.Pending, TwoErrors);
        await SeedAsync(CardsStatus.Done, TwoErrors);
        await SeedAsync(CardsStatus.Failed, TwoErrors);
        await SeedAsync(null, TwoErrors);
        var last = await SeedAsync(CardsStatus.Pending, TwoErrors);

        var pending = await Service(new StubLlmProvider(), out var ctx).PendingAsync();
        await ctx.DisposeAsync();

        Assert.Equal([first, last], pending);
    }

    [Fact]
    public async Task Giving_up_marks_a_pending_practice_as_failed()
    {
        var id = await SeedAsync(CardsStatus.Pending, TwoErrors);

        await Service(new StubLlmProvider(), out var ctx).MarkFailedAsync(id);
        await ctx.DisposeAsync();

        await using var check = _db.NewContext();
        Assert.Equal(CardsStatus.Failed, (await check.Exercises.SingleAsync()).CardsStatus);
    }

    /// <summary>
    /// Falha só volta pra fila por pedido explícito — e volta toda. Prática feita não
    /// é tocada: reprocessar custaria de novo e duplicaria a reincidência.
    /// </summary>
    [Fact]
    public async Task Retrying_puts_only_the_failed_practices_back_in_the_queue()
    {
        await SeedAsync(CardsStatus.Failed, TwoErrors);
        await SeedAsync(CardsStatus.Failed, TwoErrors);
        await SeedAsync(CardsStatus.Done, TwoErrors);

        var retried = await Service(new StubLlmProvider(), out var ctx).RetryFailedAsync();
        await ctx.DisposeAsync();

        Assert.Equal(2, retried);
        await using var check = _db.NewContext();
        Assert.Equal(2, await check.Exercises.CountAsync(p => p.CardsStatus == CardsStatus.Pending));
        Assert.Equal(1, await check.Exercises.CountAsync(p => p.CardsStatus == CardsStatus.Done));
    }

    /// <summary>
    /// O deck é o único lugar onde uma falha da cunhagem aparece — sem estes números,
    /// "a prática não deu card" e "a prática falhou" seriam indistinguíveis.
    /// </summary>
    [Fact]
    public async Task The_deck_counts_pending_and_failed_practices()
    {
        await SeedAsync(CardsStatus.Pending, TwoErrors);
        await SeedAsync(CardsStatus.Failed, TwoErrors);
        await SeedAsync(CardsStatus.Failed, TwoErrors);
        await SeedAsync(CardsStatus.Done, TwoErrors);

        await using var ctx = _db.NewContext();
        var summary = (await new CardService(ctx).GetDeckAsync()).Summary;

        Assert.Equal(1, summary.PendingPractices);
        Assert.Equal(2, summary.FailedPractices);
    }

    public void Dispose() => _db.Dispose();
}
