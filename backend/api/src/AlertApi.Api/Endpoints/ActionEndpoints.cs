using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using AlertApi.Application.DTOs.Requests;
using AlertApi.Application.DTOs.Responses;
using AlertApi.Application.Interfaces;

namespace AlertApi.Api.Endpoints;

public static class ActionEndpoints
{
    public static IEndpointRouteBuilder MapActionEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("api/actions", GetActions)
            .WithTags("Actions")
            .WithName("GetActions")
            .WithSummary("Lists the registered runbook actions with their risk and default target.");

        var group = app.MapGroup("api/action-targets").WithTags("Actions");

        group.MapGet("", GetAll)
            .WithName("GetActionTargets")
            .WithSummary("Lists the systems runbook actions may call, with their origins and operations.");

        group.MapGet("{id}", GetById)
            .WithName("GetActionTargetById")
            .WithSummary("Returns one action target.");

        group.MapPut("{id}", Put)
            .WithName("PutActionTarget")
            .WithSummary("Creates or replaces a target; 409 when the change would break an active runbook.");

        return app;
    }

    private static Ok<IReadOnlyList<ActionResponse>> GetActions(IActionTargetService service)
    {
        var actions = service.GetActions();

        return TypedResults.Ok(actions);
    }

    private static async Task<Ok<IReadOnlyList<ActionTargetResponse>>> GetAll(
        IActionTargetService service,
        CancellationToken cancellationToken)
    {
        var targets = await service.GetAllAsync(cancellationToken);

        return TypedResults.Ok(targets);
    }

    private static async Task<Ok<ActionTargetResponse>> GetById(
        string id,
        IActionTargetService service,
        CancellationToken cancellationToken)
    {
        var target = await service.GetByIdAsync(id, cancellationToken);

        return TypedResults.Ok(target);
    }

    private static async Task<Ok<ActionTargetResponse>> Put(
        string id,
        [FromBody] PutActionTargetRequest request,
        IActionTargetService service,
        CancellationToken cancellationToken)
    {
        var target = await service.PutAsync(id, request, cancellationToken);

        return TypedResults.Ok(target);
    }
}
