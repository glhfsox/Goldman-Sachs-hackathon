using System.Text.Json;

using AlertApi.Application.DTOs.Messages;
using AlertApi.Application.Interfaces;
using AlertApi.Application.Serialization;
using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;
using AlertApi.Domain.ValueObjects;

namespace AlertApi.Application.Services;

public sealed class ActivityService : IActivityService
{
    // Published often and not a timeline kind in contracts/schemas/api.md: only the alert copy changes.
    private const string CountThreshold = "count_threshold";

    private readonly IAlertRepository _alerts;
    private readonly IAlertEventRepository _events;
    private readonly INotificationRepository _notifications;
    private readonly IUnitOfWork _unitOfWork;

    public ActivityService(
        IAlertRepository alerts,
        IAlertEventRepository events,
        INotificationRepository notifications,
        IUnitOfWork unitOfWork)
    {
        _alerts = alerts;
        _events = events;
        _notifications = notifications;
        _unitOfWork = unitOfWork;
    }

    public async Task ApplyAlertChangeAsync(AlertChangeMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.Change != CountThreshold)
        {
            // The timeline row and the alert copy are saved together, so a seen change_id means both are done.
            if (await _events.ExistsAsync(message.ChangeId, cancellationToken))
            {
                return;
            }

            var data = JsonSerializer.Serialize(
                new { severity = message.Alert.Severity, count = message.Alert.Count },
                RecordJson.Options);
            await _events.AddAsync(
                AlertEvent.Create(message.ChangeId, message.Alert.Id, message.At, message.Actor, message.Change, data),
                cancellationToken);
        }

        var snapshot = ToSnapshot(message.Alert);
        var alert = await _alerts.GetByIdAsync(snapshot.Id, cancellationToken);

        if (alert is null)
        {
            await _alerts.AddAsync(Alert.Create(snapshot), cancellationToken);
        }
        else
        {
            alert.ReplaceWith(snapshot);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task ApplyTimelineEntryAsync(TimelineEntryMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (await _events.ExistsAsync(message.EntryId, cancellationToken))
        {
            return;
        }

        var data = JsonSerializer.Serialize(message.Data, RecordJson.Options);
        await _events.AddAsync(
            AlertEvent.Create(message.EntryId, message.AlertId, message.At, message.Actor, message.Kind, data),
            cancellationToken);

        var delivery = message.Data.Notification;
        var notification = await _notifications.GetByIdAsync(delivery.Id, cancellationToken);

        if (notification is null)
        {
            await _notifications.AddAsync(
                Notification.Create(
                    delivery.Id,
                    message.AlertId,
                    delivery.UserId,
                    delivery.Channel,
                    delivery.Status,
                    delivery.Reason,
                    delivery.SendAt,
                    delivery.SentAt,
                    delivery.Error),
                cancellationToken);
        }
        else
        {
            notification.Update(
                delivery.UserId,
                delivery.Channel,
                delivery.Status,
                delivery.Reason,
                delivery.SendAt,
                delivery.SentAt,
                delivery.Error);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private static AlertSnapshot ToSnapshot(AlertMessage alert)
    {
        return new AlertSnapshot(
            alert.Id,
            alert.RuleId,
            alert.TeamId,
            alert.IncidentId,
            alert.Status,
            alert.Severity,
            alert.Title,
            alert.Body,
            alert.Fields.GetRawText(),
            alert.Color,
            alert.Count,
            alert.FirstSeen,
            alert.LastSeen,
            alert.FirstReceivedAt,
            alert.RunbookId,
            alert.AckedBy,
            alert.ResolvedBy,
            alert.SampleEvent.GetRawText());
    }
}
