namespace Notifier.Application.Options;

public sealed class CleanupOptions
{
    public const string SectionName = "Cleanup";

    public int IntervalMinutes { get; init; } = 10;

    // Published records are only kept for troubleshooting.
    public int OutboxRetentionHours { get; init; } = 24;

    // Longer than any redelivery of alerts.changes is expected to arrive.
    public int ProcessedChangeRetentionHours { get; init; } = 168;

    // Only rows in a final status (sent, dead, cancelled, suppressed) are deleted.
    public int NotificationRetentionHours { get; init; } = 168;
}
