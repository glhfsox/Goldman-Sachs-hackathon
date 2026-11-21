namespace AlertApi.Application.DTOs.Messages;

// One record of topic alerts.changes (contracts/events.md section 4).
public sealed record AlertChangeMessage(
    string ChangeId,
    string Change,
    DateTimeOffset At,
    string Actor,
    AlertMessage Alert);
