using Microsoft.AspNetCore.Http.HttpResults;
using WriteRight.Api.Services;
using WriteRight.Shared.Practices;
using WriteRight.Shared.Profile;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Api.Endpoints;

public static class PracticeEndpoints
{
    public static void MapPracticeEndpoints(this IEndpointRouteBuilder app)
    {
        var practices = app.MapGroup("/api/practices").WithTags("Practices");

        practices.MapPost("", CreateAsync)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status502BadGateway);
        practices.MapGet("", ListAsync);
        practices.MapGet("/completions", ListCompletionTimesAsync);
        practices.MapGet("/{id:int}", GetAsync);
        practices.MapPut("/{id:int}/translation", SaveDraftAsync);
        practices.MapPost("/{id:int}/correct", CorrectAsync)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status502BadGateway);
        practices.MapDelete("/{id:int}", DeleteAsync);

        var profile = app.MapGroup("/api/profile").WithTags("Profile");

        profile.MapGet("", GetProfileAsync);
        profile.MapGet("/errors", GetCategoryErrorsAsync);
    }

    /// <summary>Gera o texto e persiste a prática como "Em andamento".</summary>
    private static async Task<Created<PracticeDetail>> CreateAsync(
        CreatePracticeRequest request, PracticeService service, CancellationToken ct)
    {
        var detail = await service.CreatePracticeAsync(request, ct);
        return TypedResults.Created($"/api/practices/{detail.Id}", detail);
    }

    /// <summary>Uma página da listagem, a partir de <c>?skip=</c>.</summary>
    private static async Task<Ok<PracticePage>> ListAsync(
        int? skip, PracticeService service, CancellationToken ct) =>
        TypedResults.Ok(await service.ListPracticesAsync(skip ?? 0, ct: ct));

    /// <summary>
    /// Quando cada prática concluída foi concluída. A série de dias é calculada no
    /// cliente, que é quem sabe o fuso.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<DateTimeOffset>>> ListCompletionTimesAsync(
        PracticeService service, CancellationToken ct) =>
        TypedResults.Ok(await service.ListCompletionTimesAsync(ct));

    private static async Task<Results<Ok<PracticeDetail>, NotFound>> GetAsync(
        int id, PracticeService service, CancellationToken ct) =>
        await service.GetPracticeAsync(id, ct) is { } detail
            ? TypedResults.Ok(detail)
            : TypedResults.NotFound();

    /// <summary>Salva o rascunho ("Salvar e sair"), sem corrigir.</summary>
    private static async Task<Results<NoContent, Conflict, NotFound>> SaveDraftAsync(
        int id, PracticeTranslationRequest request, PracticeService service, CancellationToken ct) =>
        await service.SaveDraftAsync(id, request.UserTranslation, ct) switch
        {
            PracticeOutcome.Ok => TypedResults.NoContent(),
            PracticeOutcome.ReadOnly => TypedResults.Conflict(),
            _ => TypedResults.NotFound(),
        };

    /// <summary>Corrige e conclui a prática (readonly). 409 se ela já estava concluída.</summary>
    private static async Task<Results<Ok<PracticeDetail>, Conflict, NotFound>> CorrectAsync(
        int id, PracticeTranslationRequest request, PracticeService service, CancellationToken ct)
    {
        var (outcome, detail) = await service.CorrectPracticeAsync(id, request.UserTranslation, ct);
        return outcome switch
        {
            PracticeOutcome.Ok => TypedResults.Ok(detail!),
            PracticeOutcome.ReadOnly => TypedResults.Conflict(),
            _ => TypedResults.NotFound(),
        };
    }

    /// <summary>Exclusão lógica — ver <see cref="PracticeService.DeletePracticeAsync"/>.</summary>
    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        int id, PracticeService service, CancellationToken ct) =>
        await service.DeletePracticeAsync(id, ct) == PracticeOutcome.Ok
            ? TypedResults.NoContent()
            : TypedResults.NotFound();

    private static async Task<Ok<ErrorProfile>> GetProfileAsync(
        PracticeService service, CancellationToken ct) =>
        TypedResults.Ok(await service.GetProfileAsync(ct));

    /// <summary>Os erros reais de uma categoria (revisão do perfil). Releitura, sem IA.</summary>
    private static async Task<Ok<IReadOnlyList<CategoryError>>> GetCategoryErrorsAsync(
        ErrorCategory category, PracticeService service, CancellationToken ct) =>
        TypedResults.Ok(await service.GetCategoryErrorsAsync(category, ct));
}
