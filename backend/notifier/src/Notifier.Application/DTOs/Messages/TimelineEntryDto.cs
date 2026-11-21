namespace Notifier.Application.DTOs.Messages;

// One record of topic alerts.timeline (contracts/events.md §7).
public sealed record TimelineEntryDto(
    string EntryId,
    string AlertId,
    DateTime At,
    string Actor,
    string Kind,
    TimelineDataDto Data);
