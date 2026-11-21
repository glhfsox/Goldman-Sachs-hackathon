using Notifier.Domain.ValueObjects;

namespace Notifier.Domain.Models;

// Copy of the processor's alert, fed by alerts.changes (contracts/schemas/notifier.md).
public class Alert
{
    private Alert()
    {
    }

    public string Id { get; private set; } = null!;

    public string? RuleId { get; private set; }

    public string? IncidentId { get; private set; }

    public AlertStatus Status { get; private set; }

    public string Severity { get; private set; } = null!;

    // The latest Alert object received, unchanged.
    public string Body { get; private set; } = null!;

    public DateTime UpdatedAt { get; private set; }

    public bool IsOpen => Status == AlertStatus.Open;

    public static Alert Create(
        string id,
        string? ruleId,
        string? incidentId,
        AlertStatus status,
        string severity,
        string body,
        DateTime changedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(severity);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        return new Alert
        {
            Id = id,
            RuleId = ruleId,
            IncidentId = incidentId,
            Status = status,
            Severity = severity,
            Body = body,
            UpdatedAt = changedAt
        };
    }

    // Changes of one alert can arrive out of order after a rebalance; an older one never overwrites a newer one.
    public bool Apply(
        AlertStatus status,
        string severity,
        string? incidentId,
        string body,
        DateTime changedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(severity);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        if (changedAt < UpdatedAt)
        {
            return false;
        }

        Status = status;
        Severity = severity;
        IncidentId = incidentId ?? IncidentId;
        Body = body;
        UpdatedAt = changedAt;

        return true;
    }
}
