using System.Text.Json;

using Notifier.Application.Constants;
using Notifier.Application.DTOs.Messages;
using Notifier.Application.Serialization;
using Notifier.Domain.Models;
using Notifier.Domain.Repositories;

namespace Notifier.Application.Services;

// alerts.timeline records go through the outbox, so they commit together with the notification state they describe
// (contracts/storage.md: change the database and publish atomically).
public sealed class TimelineService : ITimelineService
{
    private const string Actor = "notifier";

    private readonly IOutboxRepository _outbox;

    public TimelineService(IOutboxRepository outbox)
    {
        _outbox = outbox;
    }

    public async Task AppendAsync(Notification notification, string kind, DateTime now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        var snapshot = new NotificationSnapshotDto(
            notification.Id,
            notification.UserId,
            notification.Channel,
            notification.Status.ToString().ToLowerInvariant(),
            notification.Reason,
            notification.SendAt,
            notification.SentAt,
            notification.Error);
        var entry = new TimelineEntryDto(
            Guid.NewGuid().ToString("N"),
            notification.AlertId,
            now,
            Actor,
            kind,
            new TimelineDataDto(snapshot));
        var payload = JsonSerializer.Serialize(entry, NotifierJson.Options);

        await _outbox.AddAsync(OutboxMessage.Create(Topics.AlertsTimeline, notification.AlertId, payload, now), cancellationToken);
    }
}
