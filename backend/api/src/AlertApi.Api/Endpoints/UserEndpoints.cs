using System.Text.Json;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using AlertApi.Application.DTOs.Responses;
using AlertApi.Application.Interfaces;

namespace AlertApi.Api.Endpoints;

public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("api/users").WithTags("Users");

        group.MapGet("", GetAll)
            .WithName("GetUsers")
            .WithSummary("Lists the users for the user picker.");

        group.MapGet("{id}/preferences", GetPreferences)
            .WithName("GetUserPreferences")
            .WithSummary("Returns the channel preferences of one user (contracts/rules.md section 5).");

        group.MapPut("{id}/preferences", SavePreferences)
            .WithName("SaveUserPreferences")
            .WithSummary("Replaces the channel preferences of one user; 422 invalid_preferences names every wrong path.");

        return app;
    }

    private static async Task<Ok<IReadOnlyList<UserResponse>>> GetAll(
        IReferenceDataQueries queries,
        CancellationToken cancellationToken)
    {
        var users = await queries.GetUsersAsync(cancellationToken);

        return TypedResults.Ok(users);
    }

    private static async Task<Ok<JsonElement>> GetPreferences(
        string id,
        IPreferencesService service,
        CancellationToken cancellationToken)
    {
        var preferences = await service.GetAsync(id, cancellationToken);

        return TypedResults.Ok(preferences);
    }

    private static async Task<Ok<JsonElement>> SavePreferences(
        string id,
        [FromBody] JsonElement request,
        IPreferencesService service,
        CancellationToken cancellationToken)
    {
        var preferences = await service.SaveAsync(id, request, cancellationToken);

        return TypedResults.Ok(preferences);
    }
}
