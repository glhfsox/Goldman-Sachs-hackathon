using System.Text.Json;

namespace Notifier.Application.DTOs.Messages;

// One record of topic alerts.changes (contracts/events.md §4).
public sealed record AlertChangeMessage(
    string ChangeId,
    string Change,
    DateTime At,
    string Actor,
    JsonElement Alert);
