using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using AlertApi.Application.DTOs.Requests;
using AlertApi.Application.DTOs.Responses;
using AlertApi.Application.Interfaces;

namespace AlertApi.Api.Endpoints;

public static class RunbookEndpoints
{
    public static IEndpointRouteBuilder MapRunbookEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("api/runbooks").WithTags("Runbooks");

        group.MapGet("", GetAll)
            .WithName("GetRunbooks")
            .WithSummary("Lists runbooks, filter with ?team=.");

        group.MapGet("{id}", GetById)
            .WithName("GetRunbookById")
            .WithSummary("Returns one runbook with its source and canonical form.");

        group.MapPost("", Create)
            .WithName("CreateRunbook")
            .WithSummary("Uploads a runbook as a draft; the ai-worker converts it. Published on config.changes.");

        group.MapPut("{id}", Update)
            .WithName("UpdateRunbook")
            .WithSummary("Saves a canonical form (a new version) and/or a status. Published on config.changes.");

        group.MapPost("{id}/accept", Accept)
            .WithName("AcceptRunbook")
            .WithSummary("A person (X-User) accepts ?version=; 403 for a service, 409 when not active or not the current version.");

        return app;
    }

    private static async Task<Ok<IReadOnlyList<RunbookResponse>>> GetAll(
        [FromQuery] string? team,
        IRunbookService service,
        CancellationToken cancellationToken)
    {
        var runbooks = await service.GetAllAsync(team, cancellationToken);

        return TypedResults.Ok(runbooks);
    }

    private static async Task<Ok<RunbookResponse>> GetById(
        string id,
        IRunbookService service,
        CancellationToken cancellationToken)
    {
        var runbook = await service.GetByIdAsync(id, cancellationToken);

        return TypedResults.Ok(runbook);
    }

    private static async Task<CreatedAtRoute<RunbookResponse>> Create(
        [FromBody] CreateRunbookRequest request,
        IRunbookService service,
        CancellationToken cancellationToken)
    {
        var runbook = await service.CreateAsync(request, cancellationToken);

        return TypedResults.CreatedAtRoute(runbook, "GetRunbookById", new { id = runbook.Id });
    }

    private static async Task<Ok<RunbookResponse>> Update(
        string id,
        [FromBody] UpdateRunbookRequest request,
        IRunbookService service,
        CancellationToken cancellationToken)
    {
        var runbook = await service.UpdateAsync(id, request, cancellationToken);

        return TypedResults.Ok(runbook);
    }

    private static async Task<Ok<RunbookResponse>> Accept(
        string id,
        [FromHeader(Name = "X-User")] string? user,
        [FromQuery] int? version,
        IRunbookService service,
        CancellationToken cancellationToken)
    {
        var runbook = await service.AcceptAsync(id, user, version, cancellationToken);

        return TypedResults.Ok(runbook);
    }
}
