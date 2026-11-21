using System.Text.Json;

namespace Ingest.Domain.ValueObjects;

// The record on events.raw (contracts/events.md section 2). Payload is the source message as received.
public sealed record RawEvent(
    Guid EventId,
    string SourceId,
    DateTimeOffset ReceivedAt,
    JsonElement Payload);
