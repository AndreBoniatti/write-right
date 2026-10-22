using Microsoft.AspNetCore.Http.HttpResults;
using WriteRight.Api.Services;
using WriteRight.Shared.Usage;

namespace WriteRight.Api.Endpoints;

public static class UsageEndpoints
{
    public static void MapUsageEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/usage", GetReportAsync).WithTags("Usage");

    /// <summary>Quanto a IA custou, por operação, e a média por prática e por análise.</summary>
    private static async Task<Ok<UsageReport>> GetReportAsync(UsageService service, CancellationToken ct) =>
        TypedResults.Ok(await service.GetReportAsync(ct));
}
