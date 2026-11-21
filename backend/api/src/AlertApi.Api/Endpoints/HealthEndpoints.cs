using Microsoft.AspNetCore.Http.HttpResults;

namespace AlertApi.Api.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("health");

        group.MapGet("", Get)
            .WithName(nameof(Get))
            .WithSummary("Reports that the service is running.")
            .WithTags("Health");

        return app;
    }

    private static Task<Ok<HealthResponse>> Get()
    {
        return Task.FromResult(TypedResults.Ok(new HealthResponse("ok")));
    }
}

public sealed record HealthResponse(string Status);
