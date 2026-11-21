using System.Text;
using System.Text.Json;

using Ingest.Application.DTOs.Responses;
using Ingest.Application.Exceptions;
using Ingest.Application.Interfaces;
using Ingest.Domain.Models;
using Ingest.Domain.ValueObjects;

namespace Ingest.Application.Services;

public sealed class IngestService : IIngestService
{
    // Limit from contracts/data/sources.md; senders send 0.2 to 2 KiB.
    public const int MaxBodyBytes = 64 * 1024;

    private readonly ISourceRegistry _sourceRegistry;
    private readonly IRawEventPublisher _publisher;
    private readonly TimeProvider _timeProvider;

    public IngestService(
        ISourceRegistry sourceRegistry,
        IRawEventPublisher publisher,
        TimeProvider timeProvider)
    {
        _sourceRegistry = sourceRegistry;
        _publisher = publisher;
        _timeProvider = timeProvider;
    }

    public async Task<IngestResponse> IngestHttpAsync(
        string sourceId,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        EnsureHttpSourceIsEnabled(sourceId);

        if (body.Length > MaxBodyBytes)
        {
            throw new EventTooLargeException(MaxBodyBytes);
        }

        var rawEvent = new RawEvent(
            Guid.CreateVersion7(),
            sourceId,
            _timeProvider.GetUtcNow(),
            ParsePayload(body.Span));

        await _publisher.PublishAsync(rawEvent, cancellationToken);

        return new IngestResponse(rawEvent.EventId);
    }

    private void EnsureHttpSourceIsEnabled(string sourceId)
    {
        var source = _sourceRegistry.Find(sourceId);

        // A kafka source is read from its topic only, so the webhook answers 404 for it too.
        if (source is null || !source.Enabled || source.Kind != SourceKind.Http)
        {
            throw new SourceNotFoundException(sourceId);
        }
    }

    // Valid JSON stays JSON; anything else (a log line, broken JSON) becomes a string.
    // Invalid UTF-8 bytes become U+FFFD so that one bad byte never loses the event.
    private static JsonElement ParsePayload(ReadOnlySpan<byte> body)
    {
        var text = Encoding.UTF8.GetString(body);

        try
        {
            using var document = JsonDocument.Parse(text);

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(text);
        }
    }
}
