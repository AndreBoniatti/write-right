using Microsoft.AspNetCore.Http.HttpResults;
using WriteRight.Api.Services;
using WriteRight.Shared.Cards;

namespace WriteRight.Api.Endpoints;

/// <summary>
/// Deck de vocabulário. Nenhum endpoint aqui chama a IA — quem desenha os cards é o
/// <see cref="CardWorker"/>, depois da correção.
/// </summary>
public static class CardEndpoints
{
    private const string SummaryPath = "/api/cards/summary";

    public static void MapCardEndpoints(this IEndpointRouteBuilder app)
    {
        var cards = app.MapGroup("/api/cards").WithTags("Cards");

        cards.MapGet("/due", GetDueAsync);
        cards.MapPost("/{id:int}/check", CheckAsync);
        cards.MapPost("/{id:int}/review", ReviewAsync);
        cards.MapGet("", GetDeckAsync);
        cards.MapGet("/summary", GetSummaryAsync);
        cards.MapDelete("/{id:int}", DiscardAsync);
        cards.MapPost("/retry", RetryFailedAsync);
    }

    /// <summary>A fila da sessão, na ordem de revisão. SEM a resposta — ela só chega no check.</summary>
    private static async Task<Ok<IReadOnlyList<CardReviewItem>>> GetDueAsync(
        CardService service, CancellationToken ct) =>
        TypedResults.Ok(await service.GetDueAsync(ct));

    /// <summary>
    /// Confere a resposta digitada e revela. NÃO agenda: quem agenda é o review, depois
    /// que o usuário classifica (ou adjudica um "quase").
    /// </summary>
    private static async Task<Results<Ok<CardCheckResult>, Conflict, NotFound>> CheckAsync(
        int id, CardCheckRequest request, CardService service, CancellationToken ct)
    {
        var (outcome, result) = await service.CheckAsync(id, request.TypedAnswer, ct);
        return outcome switch
        {
            CardOutcome.Ok => TypedResults.Ok(result!),
            CardOutcome.Inactive => TypedResults.Conflict(),
            _ => TypedResults.NotFound(),
        };
    }

    /// <summary>Fecha a revisão: reprograma o card e grava a linha do log.</summary>
    private static async Task<Results<Ok<CardReviewResult>, Conflict, NotFound>> ReviewAsync(
        int id, CardReviewRequest request, CardService service, CancellationToken ct)
    {
        var (outcome, result) = await service.ReviewAsync(id, request, ct);
        return outcome switch
        {
            CardOutcome.Ok => TypedResults.Ok(result!),
            CardOutcome.Inactive => TypedResults.Conflict(),
            _ => TypedResults.NotFound(),
        };
    }

    /// <summary>O deck inteiro (contadores + cards). Tela de leitura: aqui a resposta aparece.</summary>
    private static async Task<Ok<DeckView>> GetDeckAsync(CardService service, CancellationToken ct) =>
        TypedResults.Ok(await service.GetDeckAsync(ct));

    private static async Task<Ok<DeckSummary>> GetSummaryAsync(CardService service, CancellationToken ct) =>
        TypedResults.Ok(await service.GetSummaryAsync(ct));

    /// <summary>
    /// Descarta um card ruim. Marca, não apaga — apagar faria o mesmo card renascer no
    /// próximo erro igual.
    /// </summary>
    private static async Task<Results<NoContent, NotFound>> DiscardAsync(
        int id, CardService service, CancellationToken ct) =>
        await service.DiscardAsync(id, ct) == CardOutcome.Ok
            ? TypedResults.NoContent()
            : TypedResults.NotFound();

    /// <summary>
    /// Devolve pra fila as práticas cujos cards falharam. É o único caminho de volta:
    /// falha não se repete sozinha, porque cada tentativa é uma chamada paga.
    /// </summary>
    private static async Task<Accepted> RetryFailedAsync(
        CardMintingService minting, CardJobSignal signal, CancellationToken ct)
    {
        if (await minting.RetryFailedAsync(ct) > 0) signal.Notify();
        return TypedResults.Accepted(SummaryPath);
    }
}
