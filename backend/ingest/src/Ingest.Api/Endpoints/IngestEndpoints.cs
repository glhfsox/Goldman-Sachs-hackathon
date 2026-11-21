using Microsoft.AspNetCore.Http.HttpResults;

using Ingest.Application.DTOs.Responses;
using Ingest.Application.Interfaces;
using Ingest.Application.Services;

namespace Ingest.Api.Endpoints;

public static class IngestEndpoints
{
    public static IEndpointRouteBuilder MapIngestEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("ingest");

        group.MapPost("{sourceId}", Receive)
            .WithName(nameof(Receive))
            .WithSummary("Accepts one event (JSON or text) from an HTTP source and writes it to events.raw.")
            .WithTags("Ingest");

        return app;
    }

    private static async Task<Accepted<IngestResponse>> Receive(
        string sourceId,
        HttpRequest request,
        IIngestService ingestService,
        CancellationToken cancellationToken)
    {
        // Read one byte more than the limit, so an oversized body is detected without buffering all of it.
        var body = new byte[IngestService.MaxBodyBytes + 1];
        var length = await request.Body.ReadAtLeastAsync(
            body,
            body.Length,
            throwOnEndOfStream: false,
            cancellationToken);

        var response = await ingestService.IngestHttpAsync(sourceId, body.AsMemory(0, length), cancellationToken);

        return TypedResults.Accepted(uri: (string?)null, response);
    }
}
