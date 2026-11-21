using System.Text.Json;

namespace AlertApi.Application.DTOs.Messages;

// The config.changes body of kind "runbook" (contracts/events.md §6): what the notifier and the ai-worker read.
public sealed record RunbookConfigBody(
    string Id,
    string TeamId,
    string Title,
    string Format,
    string Source,
    JsonElement Canonical,
    string Status,
    int Version);
