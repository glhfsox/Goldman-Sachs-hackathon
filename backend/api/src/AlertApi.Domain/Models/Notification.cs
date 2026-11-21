namespace AlertApi.Domain.Models;

// Copy of a notifier delivery, as reported on alerts.timeline; answers "why did I get this".
public class Notification
{
    private Notification()
    {
    }

    public string Id { get; private set; } = null!;

    public string AlertId { get; private set; } = null!;

    public string UserId { get; private set; } = null!;

    public string Channel { get; private set; } = null!;

    public string Status { get; private set; } = null!;

    public string Reason { get; private set; } = null!;

    public DateTimeOffset SendAt { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    public string? Error { get; private set; }

    public static Notification Create(
        string id,
        string alertId,
        string userId,
        string channel,
        string status,
        string reason,
        DateTimeOffset sendAt,
        DateTimeOffset? sentAt,
        string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(alertId);

        var notification = new Notification { Id = id, AlertId = alertId };
        notification.Update(userId, channel, status, reason, sendAt, sentAt, error);

        return notification;
    }

    public void Update(
        string userId,
        string channel,
        string status,
        string reason,
        DateTimeOffset sendAt,
        DateTimeOffset? sentAt,
        string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        UserId = userId;
        Channel = channel;
        Status = status;
        Reason = reason;
        SendAt = sendAt;
        SentAt = sentAt;
        Error = error;
    }
}
