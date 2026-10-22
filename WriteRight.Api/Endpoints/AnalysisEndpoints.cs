using Microsoft.AspNetCore.Http.HttpResults;
using WriteRight.Api.Services;
using WriteRight.Shared.Analysis;

namespace WriteRight.Api.Endpoints;

public static class AnalysisEndpoints
{
    private const string StatePath = "/api/analysis";

    public static void MapAnalysisEndpoints(this IEndpointRouteBuilder app)
    {
        var analysis = app.MapGroup(StatePath).WithTags("Analysis");

        analysis.MapGet("", GetStateAsync);
        analysis.MapPost("", EnqueueAsync);
    }

    /// <summary>A última análise gerada + se vale gerar outra.</summary>
    private static async Task<Ok<AnalysisState>> GetStateAsync(
        AnalysisService service, CancellationToken ct) =>
        TypedResults.Ok(await service.GetStateAsync(ct));

    /// <summary>
    /// Enfileira uma análise nova e responde 202 na hora: a chamada à IA leva minutos e
    /// não cabe numa requisição. O cliente acompanha pelo GET.
    /// </summary>
    private static async Task<Results<Accepted, Conflict>> EnqueueAsync(
        AnalysisService service, AnalysisJobQueue jobs, CancellationToken ct)
    {
        // Histórico pequeno demais: responde já, sem gastar um job pra devolver a
        // mesma negativa daqui a minutos.
        if (!await service.HasEnoughDataAsync(ct)) return TypedResults.Conflict();

        // Pedido durante uma execução também é 202: "sua análise está sendo gerada" é
        // verdade nos dois casos.
        jobs.TryEnqueue();
        return TypedResults.Accepted(StatePath);
    }
}
