using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using AlertApi.Application.DTOs.Requests;
using AlertApi.Application.DTOs.Responses;
using AlertApi.Application.Interfaces;

namespace AlertApi.Api.Endpoints;

public static class RunbookStartEndpoints
{
    public static IEndpointRouteBuilder MapRunbookStartEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("api/runbook-starts").WithTags("Runbook starts");

        group.MapGet("", GetAll)
            .WithName("GetRunbookStarts")
            .WithSummary("Lists how runs start per rule and runbook; no row means approval. Filter with ?rule= and ?runbook=.");

        group.MapPut("{ruleId}/{runbookId}", Put)
            .WithName("PutRunbookStart")
            .WithSummary("A person sets how runs of a runbook start on a rule: auto or approval. 403 for a service, 404 for an unknown rule or runbook, 422 for another mode.");

        return app;
    }

    private static async Task<Ok<IReadOnlyList<RunbookStartResponse>>> GetAll(
        [FromQuery] string? rule,
        [FromQuery] string? runbook,
        IRunbookStartService service,
        CancellationToken cancellationToken)
    {
        var result = await service.GetAllAsync(rule, runbook, cancellationToken);

        return TypedResults.Ok(result);
    }

    private static async Task<Ok<RunbookStartResponse>> Put(
        string ruleId,
        string runbookId,
        [FromBody] PutRunbookStartRequest request,
        [FromHeader(Name = "X-User")] string? user,
        IRunbookStartService service,
        CancellationToken cancellationToken)
    {
        var result = await service.PutAsync(ruleId, runbookId, request, user, cancellationToken);

        return TypedResults.Ok(result);
    }
}
