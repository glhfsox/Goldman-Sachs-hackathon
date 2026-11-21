using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using AlertApi.Application.DTOs.Requests;
using AlertApi.Application.DTOs.Responses;
using AlertApi.Application.Interfaces;

namespace AlertApi.Api.Endpoints;

public static class SchemaSampleEndpoints
{
    public static IEndpointRouteBuilder MapSchemaSampleEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("api/schema-samples").WithTags("Schema samples");

        group.MapGet("", GetAll)
            .WithName("GetSchemaSamples")
            .WithSummary("Lists the schemas the processor could not map; filter with ?status=pending|mapped|failed.");

        group.MapPut("{fingerprint}", MarkFailed)
            .WithName("MarkSchemaSampleFailed")
            .WithSummary("The ai-worker reports it could not write a mapping, so a person does; the body is {status: \"failed\", error}.");

        return app;
    }

    private static async Task<Ok<IReadOnlyList<SchemaSampleResponse>>> GetAll(
        [FromQuery] string? status,
        ISchemaSampleService service,
        CancellationToken cancellationToken)
    {
        var samples = await service.GetAllAsync(status, cancellationToken);

        return TypedResults.Ok(samples);
    }

    private static async Task<Ok<SchemaSampleResponse>> MarkFailed(
        string fingerprint,
        [FromBody] FailSchemaSampleRequest request,
        ISchemaSampleService service,
        CancellationToken cancellationToken)
    {
        var sample = await service.MarkFailedAsync(fingerprint, request, cancellationToken);

        return TypedResults.Ok(sample);
    }
}
