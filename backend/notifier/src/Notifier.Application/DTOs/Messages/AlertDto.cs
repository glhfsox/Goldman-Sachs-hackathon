using System.Text.Json;

namespace Notifier.Application.DTOs.Messages;

// The fields of the Alert object (contracts/api.md) that Notifier reads; the full object is kept as the copy's body.
public sealed record AlertDto(
    string Id,
    string? RuleId,
    string? TeamId,
    string? IncidentId,
    string? Status,
    string? Severity,
    string? Title,
    string? Body,
    Dictionary<string, JsonElement>? Fields,
    string? Color,
    string? RunbookId,
    JsonElement? SampleEvent);
