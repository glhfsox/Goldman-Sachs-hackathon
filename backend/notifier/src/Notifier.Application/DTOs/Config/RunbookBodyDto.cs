using System.Text.Json;

namespace Notifier.Application.DTOs.Config;

// config.changes body of kind "runbook"; `source` and `format` are not needed to send messages.
public sealed record RunbookBodyDto(
    string Id,
    string? TeamId,
    string? Title,
    JsonElement? Canonical,
    string? Status,
    int Version);
