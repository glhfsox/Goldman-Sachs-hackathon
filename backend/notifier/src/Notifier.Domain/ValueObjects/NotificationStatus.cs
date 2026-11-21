namespace Notifier.Domain.ValueObjects;

public enum NotificationStatus
{
    Pending,
    Sent,
    Failed,
    Dead,
    Cancelled,
    Suppressed
}
