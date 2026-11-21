namespace AlertApi.Domain.ValueObjects;

// The Alert object of contracts/api.md as it arrives on alerts.changes; json fields stay raw text.
public sealed record AlertSnapshot(
    string Id,
    string RuleId,
    string TeamId,
    string? IncidentId,
    string Status,
    string Severity,
    string Title,
    string Body,
    string Fields,
    string? Color,
    long Count,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    DateTimeOffset FirstReceivedAt,
    string? RunbookId,
    string? AckedBy,
    string? ResolvedBy,
    string SampleEvent);
