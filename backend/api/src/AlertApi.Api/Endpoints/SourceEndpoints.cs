using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using AlertApi.Application.DTOs.Requests;
using AlertApi.Application.DTOs.Responses;
using AlertApi.Application.Interfaces;

namespace AlertApi.Api.Endpoints;

public static class SourceEndpoints
{
    public static IEndpointRouteBuilder MapSourceEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("api/sources").WithTags("Sources");

        group.MapGet("", GetAll)
            .WithName(nameof(GetAll))
            .WithSummary("Lists the sources ingest reads from.");

        group.MapGet("{id}", GetById)
            .WithName(nameof(GetById))
            .WithSummary("Returns one source.");

        group.MapPost("", Create)
            .WithName(nameof(Create))
            .WithSummary("Adds a source and publishes it on config.changes.");

        group.MapPut("{id}", Update)
            .WithName(nameof(Update))
            .WithSummary("Changes a source, or enables or disables it, and publishes it on config.changes.");

        return app;
    }

    private static async Task<Ok<IReadOnlyList<SourceResponse>>> GetAll(
        ISourceService service,
        CancellationToken cancellationToken)
    {
        var sources = await service.GetAllAsync(cancellationToken);

        return TypedResults.Ok(sources);
    }

    private static async Task<Ok<SourceResponse>> GetById(
        string id,
        ISourceService service,
        CancellationToken cancellationToken)
    {
        var source = await service.GetByIdAsync(id, cancellationToken);

        return TypedResults.Ok(source);
    }

    private static async Task<CreatedAtRoute<SourceResponse>> Create(
        [FromBody] CreateSourceRequest request,
        ISourceService service,
        CancellationToken cancellationToken)
    {
        var source = await service.CreateAsync(request, cancellationToken);

        return TypedResults.CreatedAtRoute(source, nameof(GetById), new { id = source.Id });
    }

    private static async Task<Ok<SourceResponse>> Update(
        string id,
        [FromBody] UpdateSourceRequest request,
        ISourceService service,
        CancellationToken cancellationToken)
    {
        var source = await service.UpdateAsync(id, request, cancellationToken);

        return TypedResults.Ok(source);
    }
}
