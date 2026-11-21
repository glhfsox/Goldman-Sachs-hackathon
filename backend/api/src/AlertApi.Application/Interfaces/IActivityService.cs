using AlertApi.Application.DTOs.Messages;

namespace AlertApi.Application.Interfaces;

// Applies records from alerts.changes and alerts.timeline to the api's read copies. Safe to repeat.
public interface IActivityService
{
    Task ApplyAlertChangeAsync(AlertChangeMessage message, CancellationToken cancellationToken = default);

    Task ApplyTimelineEntryAsync(TimelineEntryMessage message, CancellationToken cancellationToken = default);

    // Applies a count message from Redis ui.alerts; a message for an alert not stored yet is dropped.
    Task ApplyCountAsync(CountMessage message, CancellationToken cancellationToken = default);
}
