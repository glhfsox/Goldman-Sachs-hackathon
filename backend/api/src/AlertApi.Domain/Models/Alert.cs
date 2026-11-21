using AlertApi.Domain.ValueObjects;

namespace AlertApi.Domain.Models;

// Read copy of the processor's alert (contracts/schemas/api.md). Only alerts.changes records change it.
public class Alert
{
    public const string Open = "open";
    public const string Acked = "acked";
    public const string Resolved = "resolved";

    private Alert()
    {
    }

    public string Id { get; private set; } = null!;

    public string RuleId { get; private set; } = null!;

    public string TeamId { get; private set; } = null!;

    public string? IncidentId { get; private set; }

    public string Status { get; private set; } = null!;

    public string Severity { get; private set; } = null!;

    public string Title { get; private set; } = null!;

    public string Body { get; private set; } = null!;

    public string Fields { get; private set; } = null!;

    public string? Color { get; private set; }

    public long Count { get; private set; }

    public DateTimeOffset FirstSeen { get; private set; }

    public DateTimeOffset LastSeen { get; private set; }

    public DateTimeOffset FirstReceivedAt { get; private set; }

    public string? RunbookId { get; private set; }

    public string? AckedBy { get; private set; }

    public string? ResolvedBy { get; private set; }

    public string SampleEvent { get; private set; } = null!;

    public static Alert Create(AlertSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Id);

        var alert = new Alert { Id = snapshot.Id };
        alert.ReplaceWith(snapshot);

        return alert;
    }

    public AlertSnapshot ToSnapshot()
    {
        return new AlertSnapshot(
            Id, RuleId, TeamId, IncidentId, Status, Severity, Title, Body, Fields, Color, Count,
            FirstSeen, LastSeen, FirstReceivedAt, RunbookId, AckedBy, ResolvedBy, SampleEvent);
    }

    // Redis count messages arrive out of order with Kafka records and may repeat; only a larger value moves a counter.
    public void ApplyCount(long count, DateTimeOffset lastSeen)
    {
        if (count > Count)
        {
            Count = count;
        }

        if (lastSeen > LastSeen)
        {
            LastSeen = lastSeen;
        }
    }

    // Every change record carries the whole alert, so applying one is a replace: applying it twice changes nothing.
    public void ReplaceWith(AlertSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.RuleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.TeamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Status);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Severity);

        RuleId = snapshot.RuleId;
        TeamId = snapshot.TeamId;
        IncidentId = snapshot.IncidentId;
        Status = snapshot.Status;
        Severity = snapshot.Severity;
        Title = snapshot.Title;
        Body = snapshot.Body;
        Fields = snapshot.Fields;
        Color = snapshot.Color;
        Count = snapshot.Count;
        FirstSeen = snapshot.FirstSeen;
        LastSeen = snapshot.LastSeen;
        FirstReceivedAt = snapshot.FirstReceivedAt;
        RunbookId = snapshot.RunbookId;
        AckedBy = snapshot.AckedBy;
        ResolvedBy = snapshot.ResolvedBy;
        SampleEvent = snapshot.SampleEvent;
    }
}
