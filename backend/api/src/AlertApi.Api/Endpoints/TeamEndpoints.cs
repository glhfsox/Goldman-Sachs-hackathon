using Microsoft.AspNetCore.Http.HttpResults;

using AlertApi.Application.DTOs.Responses;
using AlertApi.Application.Interfaces;

namespace AlertApi.Api.Endpoints;

public static class TeamEndpoints
{
    public static IEndpointRouteBuilder MapTeamEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("api/teams").WithTags("Teams");

        group.MapGet("", GetAll)
            .WithName("GetTeams")
            .WithSummary("Lists the teams with their members and who is on call.");

        return app;
    }

    private static async Task<Ok<IReadOnlyList<TeamResponse>>> GetAll(
        IReferenceDataQueries queries,
        CancellationToken cancellationToken)
    {
        var teams = await queries.GetTeamsAsync(cancellationToken);

        return TypedResults.Ok(teams);
    }
}
