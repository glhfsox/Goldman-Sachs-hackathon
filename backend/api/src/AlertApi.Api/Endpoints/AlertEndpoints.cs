using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using AlertApi.Application.DTOs.Responses;
using AlertApi.Application.Interfaces;

namespace AlertApi.Api.Endpoints;

public static class AlertEndpoints
{
    public static IEndpointRouteBuilder MapAlertEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("api/alerts").WithTags("Alerts");

        group.MapGet("", GetAll)
            .WithName("GetAlerts")
            .WithSummary("Lists alerts, newest first (by first_seen); filter with ?team= and ?status=, at most 200 per call.");

        group.MapGet("{id}", GetById)
            .WithName("GetAlertById")
            .WithSummary("Returns one alert with its timeline and notifications; incident and runbook are null until those features exist.");

        group.MapPost("{id}/ack", Ack)
            .WithName("AckAlert")
            .WithSummary("Acks an open alert as the user in X-User; 409 when it is not open. The copy changes when the record returns from Kafka.");

        group.MapPost("{id}/resolve", Resolve)
            .WithName("ResolveAlert")
            .WithSummary("Resolves an alert as the user in X-User; 409 when it is already resolved.");

        return app;
    }

    private static async Task<Ok<IReadOnlyList<AlertResponse>>> GetAll(
        [FromQuery] string? team,
        [FromQuery] string? status,
        [FromQuery] int? limit,
        IAlertService service,
        CancellationToken cancellationToken)
    {
        var alerts = await service.GetAllAsync(team, status, limit ?? 50, cancellationToken);

        return TypedResults.Ok(alerts);
    }

    private static async Task<Ok<AlertDetailResponse>> GetById(
        string id,
        IAlertService service,
        CancellationToken cancellationToken)
    {
        var alert = await service.GetByIdAsync(id, cancellationToken);

        return TypedResults.Ok(alert);
    }

    private static async Task<Ok<AlertResponse>> Ack(
        string id,
        [FromHeader(Name = "X-User")] string? user,
        IAlertService service,
        CancellationToken cancellationToken)
    {
        var alert = await service.AckAsync(id, user, cancellationToken);

        return TypedResults.Ok(alert);
    }

    private static async Task<Ok<AlertResponse>> Resolve(
        string id,
        [FromHeader(Name = "X-User")] string? user,
        IAlertService service,
        CancellationToken cancellationToken)
    {
        var alert = await service.ResolveAsync(id, user, cancellationToken);

        return TypedResults.Ok(alert);
    }
}
