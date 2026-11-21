using System.Text.Json;

namespace Notifier.Application.DTOs.Messages;

// One record of topic config.changes (contracts/events.md §6).
public sealed record ConfigChangeMessage(
    string Kind,
    string Id,
    DateTime UpdatedAt,
    bool Deleted,
    JsonElement? Body);
