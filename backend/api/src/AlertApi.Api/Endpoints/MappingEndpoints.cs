using System.Text.Json;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using AlertApi.Application.Interfaces;

namespace AlertApi.Api.Endpoints;

public static class MappingEndpoints
{
    public static IEndpointRouteBuilder MapMappingEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("api/mappings").WithTags("Mappings");

        group.MapGet("", GetAll)
            .WithName("GetMappings")
            .WithSummary("Lists the schema mappings, highest priority first (contracts/rules.md section 3).");

        group.MapPut("{id}", Save)
            .WithName("SaveMapping")
            .WithSummary("Creates or replaces a mapping; 422 invalid_mapping names every wrong path. With ?fingerprint= that schema sample becomes mapped. The processor applies it within 5 s.");

        return app;
    }

    private static async Task<Ok<IReadOnlyList<JsonElement>>> GetAll(
        IMappingService service,
        CancellationToken cancellationToken)
    {
        var mappings = await service.GetAllAsync(cancellationToken);

        return TypedResults.Ok(mappings);
    }

    private static async Task<Ok<JsonElement>> Save(
        string id,
        [FromBody] JsonElement request,
        [FromQuery] string? fingerprint,
        IMappingService service,
        CancellationToken cancellationToken)
    {
        var mapping = await service.SaveAsync(id, request, fingerprint, cancellationToken);

        return TypedResults.Ok(mapping);
    }
}
