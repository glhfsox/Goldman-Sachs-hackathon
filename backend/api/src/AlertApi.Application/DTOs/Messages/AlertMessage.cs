using System.Text.Json;

namespace AlertApi.Application.DTOs.Messages;

// The Alert object inside an alerts.changes record (contracts/api.md).
public sealed record AlertMessage(
    string Id,
    string RuleId,
    string TeamId,
    string? IncidentId,
    string Status,
    string Severity,
    string Title,
    string Body,
    JsonElement Fields,
    string? Color,
    long Count,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    DateTimeOffset FirstReceivedAt,
    string? RunbookId,
    string? AckedBy,
    string? ResolvedBy,
    JsonElement SampleEvent);
