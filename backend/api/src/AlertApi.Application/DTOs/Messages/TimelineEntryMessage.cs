namespace AlertApi.Application.DTOs.Messages;

// One record of topic alerts.timeline (contracts/events.md section 7).
public sealed record TimelineEntryMessage(
    string EntryId,
    string AlertId,
    DateTimeOffset At,
    string Actor,
    string Kind,
    TimelineDataMessage Data);
