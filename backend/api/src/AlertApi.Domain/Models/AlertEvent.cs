namespace AlertApi.Domain.Models;

// One row of the alert timeline and audit log. The id is the change_id or entry_id of the Kafka record it came from.
public class AlertEvent
{
    private AlertEvent()
    {
    }

    public string Id { get; private set; } = null!;

    public string AlertId { get; private set; } = null!;

    public DateTimeOffset At { get; private set; }

    public string Actor { get; private set; } = null!;

    public string Kind { get; private set; } = null!;

    public string Data { get; private set; } = null!;

    public static AlertEvent Create(string id, string alertId, DateTimeOffset at, string actor, string kind, string data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(alertId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(data);

        return new AlertEvent
        {
            Id = id,
            AlertId = alertId,
            At = at,
            Actor = actor,
            Kind = kind,
            Data = data
        };
    }
}
