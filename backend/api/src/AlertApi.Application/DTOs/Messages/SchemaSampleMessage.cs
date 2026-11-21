using System.Text.Json;

namespace AlertApi.Application.DTOs.Messages;

// One record of topic schema.samples (contracts/events.md section 8).
public sealed record SchemaSampleMessage(
    string Fingerprint,
    string SourceId,
    DateTimeOffset SeenAt,
    JsonElement Sample);
