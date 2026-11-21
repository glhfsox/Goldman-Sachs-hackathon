namespace AlertApi.Application.DTOs.Messages;

// The count message of Redis ui.alerts (contracts/events.md section 5).
public sealed record CountMessage(
    string AlertId,
    long Count,
    DateTimeOffset LastSeen);
