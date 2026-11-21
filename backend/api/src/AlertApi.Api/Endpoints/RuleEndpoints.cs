using System.Text.Json;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using AlertApi.Application.Interfaces;

namespace AlertApi.Api.Endpoints;

public static class RuleEndpoints
{
    public static IEndpointRouteBuilder MapRuleEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Route names are global in the app, so each one carries its resource.
        var group = app.MapGroup("api/rules").WithTags("Rules");

        group.MapGet("", GetAll)
            .WithName("GetRules")
            .WithSummary("Lists the rules, optionally of one team (contracts/rules.md section 4).");

        group.MapGet("{id}", GetById)
            .WithName("GetRuleById")
            .WithSummary("Returns one rule.");

        group.MapPost("", Create)
            .WithName("CreateRule")
            .WithSummary("Adds a rule; 422 invalid_rule names every wrong path. The processor and notifier apply it within 5 s.");

        group.MapPut("{id}", Update)
            .WithName("UpdateRule")
            .WithSummary("Replaces a rule; 422 invalid_rule names every wrong path.");

        return app;
    }

    private static async Task<Ok<IReadOnlyList<JsonElement>>> GetAll(
        [FromQuery] string? team,
        IRuleService service,
        CancellationToken cancellationToken)
    {
        var rules = await service.GetAllAsync(team, cancellationToken);

        return TypedResults.Ok(rules);
    }

    private static async Task<Ok<JsonElement>> GetById(
        string id,
        IRuleService service,
        CancellationToken cancellationToken)
    {
        var rule = await service.GetByIdAsync(id, cancellationToken);

        return TypedResults.Ok(rule);
    }

    private static async Task<CreatedAtRoute<JsonElement>> Create(
        [FromBody] JsonElement request,
        IRuleService service,
        CancellationToken cancellationToken)
    {
        var rule = await service.CreateAsync(request, cancellationToken);

        return TypedResults.CreatedAtRoute(rule, "GetRuleById", new { id = rule.GetProperty("id").GetString() });
    }

    private static async Task<Ok<JsonElement>> Update(
        string id,
        [FromBody] JsonElement request,
        IRuleService service,
        CancellationToken cancellationToken)
    {
        var rule = await service.UpdateAsync(id, request, cancellationToken);

        return TypedResults.Ok(rule);
    }
}
