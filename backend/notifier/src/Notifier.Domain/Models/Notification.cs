using Notifier.Domain.Exceptions;
using Notifier.Domain.ValueObjects;

namespace Notifier.Domain.Models;

public class Notification
{
    private Notification()
    {
    }

    public string Id { get; private set; } = null!;

    public string AlertId { get; private set; } = null!;

    public string? IncidentId { get; private set; }

    // Incident id, else the triggering event id, else the alert id: what "one incident" means for one-message-per-person.
    public string GroupKey { get; private set; } = null!;

    public string UserId { get; private set; } = null!;

    public string Channel { get; private set; } = null!;

    public string ChangeId { get; private set; } = null!;

    public int EscalationStep { get; private set; }

    public string Reason { get; private set; } = null!;

    public NotificationStatus Status { get; private set; }

    public DateTime SendAt { get; private set; }

    public int Attempts { get; private set; }

    public Recipient Recipient { get; private set; } = null!;

    public NotificationContent Content { get; private set; } = null!;

    public string? Rendered { get; private set; }

    public string? ProviderId { get; private set; }

    public DateTime? SentAt { get; private set; }

    public string? Error { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime UpdatedAt { get; private set; }

    public bool IsOpen => Status is NotificationStatus.Pending or NotificationStatus.Failed;

    public static Notification Create(
        NotificationId id,
        string alertId,
        string? incidentId,
        string groupKey,
        Recipient recipient,
        Channel channel,
        string changeId,
        int escalationStep,
        string reason,
        NotificationContent content,
        DateTime sendAt,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(alertId);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupKey);
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(changeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(content);

        return new Notification
        {
            Id = id.Value,
            AlertId = alertId,
            IncidentId = incidentId,
            GroupKey = groupKey,
            UserId = recipient.UserId,
            Channel = channel.Name,
            ChangeId = changeId,
            EscalationStep = escalationStep,
            Reason = reason,
            Status = NotificationStatus.Pending,
            SendAt = sendAt,
            Recipient = recipient,
            Content = content,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public void Reroute(Channel channel, string reason, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        EnsureOpen("be rerouted");

        Channel = channel.Name;
        Reason = $"{Reason}; {reason}";
        UpdatedAt = now;
    }

    public void MarkSent(string rendered, string? providerId, DateTime now)
    {
        EnsureOpen("be sent");

        Status = NotificationStatus.Sent;
        Attempts++;
        Rendered = rendered;
        ProviderId = providerId;
        SentAt = now;
        Error = null;
        UpdatedAt = now;
    }

    public void ScheduleRetry(string error, DateTime nextAttemptAt, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        EnsureOpen("be retried");

        Status = NotificationStatus.Failed;
        Attempts++;
        Error = error;
        SendAt = nextAttemptAt;
        UpdatedAt = now;
    }

    public void MarkDead(string error, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        EnsureOpen("be given up");

        Status = NotificationStatus.Dead;
        Attempts++;
        Error = error;
        UpdatedAt = now;
    }

    public void Cancel(string reason, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        EnsureOpen("be cancelled");

        Status = NotificationStatus.Cancelled;
        Reason = $"{Reason}; cancelled: {reason}";
        UpdatedAt = now;
    }

    public void Suppress(string reason, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        EnsureOpen("be suppressed");

        Status = NotificationStatus.Suppressed;
        Reason = $"{Reason}; suppressed: {reason}";
        UpdatedAt = now;
    }

    private void EnsureOpen(string action)
    {
        if (!IsOpen)
        {
            throw new InvalidStateTransitionException(nameof(Notification), Id, Status.ToString(), action);
        }
    }
}
