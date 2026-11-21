using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using AlertApi.Application.DTOs.Requests;
using AlertApi.Application.DTOs.Responses;
using AlertApi.Application.Interfaces;

namespace AlertApi.Api.Endpoints;

public static class RunEndpoints
{
    public static IEndpointRouteBuilder MapRunEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("api/alerts/{id}/runs/preview", Preview)
            .WithTags("Runs")
            .WithName("PreviewRun")
            .WithSummary("Shows what starting a run of ?runbook_id= would do: every step with its filled params and risk.");

        app.MapPost("api/alerts/{id}/runs", Start)
            .WithTags("Runs")
            .WithName("StartRun")
            .WithSummary("Starts a run by hand as the person in X-User; that is the approval. 409 when the alert has a live run or the runbook version is not accepted.");

        var group = app.MapGroup("api/runs").WithTags("Runs");

        group.MapGet("", List)
            .WithName("ListRuns")
            .WithSummary("Lists runs for the ai-worker: ?risk_check=pending (waiting for a verdict) or ?finished_since=<time>&limit= (oldest first).");

        group.MapPost("{id}/risk-check", RiskCheck)
            .WithName("PostRunRiskCheck")
            .WithSummary("The ai-worker's verdict on a scheduled auto run: clear lets it start, hold sends it to a person. Only X-User ai-worker.");

        group.MapPost("{id}/approve", Approve)
            .WithName("ApproveRun")
            .WithSummary("A person approves a run held in waiting_approval; it starts at once.");

        group.MapGet("{id}", GetById)
            .WithName("GetRunById")
            .WithSummary("Returns a run with every step and its state.");

        group.MapPost("{id}/cancel", Cancel)
            .WithName("CancelRun")
            .WithSummary("Cancels every step not yet started; a step that is running finishes and keeps its result.");

        group.MapPost("{id}/nodes/{node}/done", Tick)
            .WithName("TickRunNode")
            .WithSummary("Ticks a text-only step as done.");

        return app;
    }

    private static async Task<Ok<RunPreviewResponse>> Preview(
        string id,
        [FromQuery(Name = "runbook_id")] string? runbookId,
        IRunService service,
        CancellationToken cancellationToken)
    {
        var preview = await service.PreviewAsync(id, runbookId, cancellationToken);

        return TypedResults.Ok(preview);
    }

    private static async Task<CreatedAtRoute<RunResponse>> Start(
        string id,
        [FromBody] StartRunRequest request,
        [FromHeader(Name = "X-User")] string? user,
        IRunService service,
        CancellationToken cancellationToken)
    {
        var run = await service.StartAsync(id, request, user, cancellationToken);

        return TypedResults.CreatedAtRoute(run, "GetRunById", new { id = run.Id });
    }

    private static async Task<Ok<RunResponse>> GetById(
        string id,
        IRunService service,
        CancellationToken cancellationToken)
    {
        var run = await service.GetByIdAsync(id, cancellationToken);

        return TypedResults.Ok(run);
    }

    private static async Task<Ok<RunResponse>> Cancel(
        string id,
        [FromHeader(Name = "X-User")] string? user,
        IRunService service,
        CancellationToken cancellationToken)
    {
        var run = await service.CancelAsync(id, user, cancellationToken);

        return TypedResults.Ok(run);
    }

    private static async Task<Ok<RunNodeResponse>> Tick(
        string id,
        string node,
        [FromHeader(Name = "X-User")] string? user,
        IRunService service,
        CancellationToken cancellationToken)
    {
        var result = await service.TickAsync(id, node, user, cancellationToken);

        return TypedResults.Ok(result);
    }

    private static async Task<Ok<IReadOnlyList<RunResponse>>> List(
        [FromQuery(Name = "risk_check")] string? riskCheck,
        [FromQuery(Name = "finished_since")] DateTimeOffset? finishedSince,
        [FromQuery] int? limit,
        IRunService service,
        CancellationToken cancellationToken)
    {
        var runs = await service.ListAsync(riskCheck, finishedSince, limit, cancellationToken);

        return TypedResults.Ok(runs);
    }

    private static async Task<Ok<RunResponse>> RiskCheck(
        string id,
        [FromBody] RiskCheckRequest request,
        [FromHeader(Name = "X-User")] string? user,
        IRunService service,
        CancellationToken cancellationToken)
    {
        var run = await service.RiskCheckAsync(id, request, user, cancellationToken);

        return TypedResults.Ok(run);
    }

    private static async Task<Ok<RunResponse>> Approve(
        string id,
        [FromHeader(Name = "X-User")] string? user,
        IRunService service,
        CancellationToken cancellationToken)
    {
        var run = await service.ApproveAsync(id, user, cancellationToken);

        return TypedResults.Ok(run);
    }
}
