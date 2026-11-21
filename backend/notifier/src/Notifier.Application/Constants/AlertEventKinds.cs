namespace Notifier.Application.Constants;

// alerts.timeline kinds (contracts/events.md §7).
public static class AlertEventKinds
{
    public const string NotificationSent = "notification_sent";
    public const string NotificationFailed = "notification_failed";
    public const string NotificationSuppressed = "notification_suppressed";
}
