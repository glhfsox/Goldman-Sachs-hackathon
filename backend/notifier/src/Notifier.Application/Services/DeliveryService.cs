using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Notifier.Application.Constants;
using Notifier.Application.DTOs.Channels;
using Notifier.Application.Interfaces;
using Notifier.Application.Options;
using Notifier.Domain.Models;
using Notifier.Domain.Repositories;
using Notifier.Domain.Services;
using Notifier.Domain.ValueObjects;

namespace Notifier.Application.Services;

// Channel-agnostic: every channel goes through the same claim -> gate -> render -> adapter -> outcome path.
public sealed class DeliveryService : IDeliveryService
{
    private readonly INotificationRepository _notifications;
    private readonly IAlertRepository _alerts;
    private readonly IRateCounterRepository _rateCounters;
    private readonly ITimelineService _timeline;
    private readonly IMessageRenderService _renderer;
    private readonly IChannelAdapterRegistry _channels;
    private readonly DeliveryGate _gate;
    private readonly FallbackPolicy _fallback;
    private readonly IClock _clock;
    private readonly DeliveryOptions _options;
    private readonly ILogger<DeliveryService> _logger;

    public DeliveryService(
        INotificationRepository notifications,
        IAlertRepository alerts,
        IRateCounterRepository rateCounters,
        ITimelineService timeline,
        IMessageRenderService renderer,
        IChannelAdapterRegistry channels,
        DeliveryGate gate,
        FallbackPolicy fallback,
        IClock clock,
        IOptions<DeliveryOptions> options,
        ILogger<DeliveryService> logger)
    {
        _notifications = notifications;
        _alerts = alerts;
        _rateCounters = rateCounters;
        _timeline = timeline;
        _renderer = renderer;
        _channels = channels;
        _gate = gate;
        _fallback = fallback;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> ClaimDueAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;

        return await _notifications.ClaimDueAsync(
            now,
            _options.BatchSize,
            now.AddSeconds(_options.LeaseSeconds),
            cancellationToken);
    }

    public async Task DeliverAsync(string notificationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notificationId);

        // Cancelled by an ack, or cleaned up, since it was claimed.
        var notification = await _notifications.FindAsync(notificationId, cancellationToken);
        if (notification is null || !notification.IsOpen)
        {
            return;
        }

        await DeliverAsync(notification, cancellationToken);
        await _notifications.SaveChangesAsync(cancellationToken);
    }

    private async Task DeliverAsync(Notification notification, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        // An ack can land between planning and sending; the alert copy is the last word.
        var alert = await _alerts.FindAsync(notification.AlertId, cancellationToken);
        if (alert is not null && !alert.IsOpen)
        {
            notification.Cancel($"suppressed by {alert.Status.ToString().ToLowerInvariant()}", now);
            await _timeline.AppendAsync(notification, AlertEventKinds.NotificationSuppressed, now, cancellationToken);

            return;
        }

        var recipient = notification.Recipient;
        var counter = await LockCounterAsync(recipient, notification.Channel, cancellationToken);
        var decision = _gate.Decide(
            recipient,
            new Channel(notification.Channel),
            Severity.ParseOrDefault(notification.Content.Severity),
            counter,
            now);

        if (decision.Suppressed)
        {
            notification.Suppress(decision.Reason!, now);
            await _timeline.AppendAsync(notification, AlertEventKinds.NotificationSuppressed, now, cancellationToken);

            return;
        }

        if (decision.Channel.Name != notification.Channel)
        {
            notification.Reroute(decision.Channel, decision.Reason!, now);
        }

        var adapter = _channels.Find(notification.Channel);
        if (adapter is null)
        {
            await GiveUpAsync(notification, $"No channel adapter is enabled for '{notification.Channel}'.", now, cancellationToken);

            return;
        }

        var folded = decision.Channel.IsPush && counter is not null ? counter.TakeFolded() : 0;
        var message = _renderer.Render(notification, folded);
        var result = await SendAsync(adapter, notification, message, cancellationToken);

        switch (result.Status)
        {
            case SendStatus.Sent:
                notification.MarkSent(message.Body, result.ProviderId, now);
                await _timeline.AppendAsync(notification, AlertEventKinds.NotificationSent, now, cancellationToken);
                break;
            case SendStatus.Retry when notification.Attempts + 1 < _options.MaxAttempts:
                notification.ScheduleRetry(result.Error ?? "temporary failure", now + Backoff(notification.Attempts + 1), now);
                await _timeline.AppendAsync(notification, AlertEventKinds.NotificationFailed, now, cancellationToken);
                break;
            default:
                await GiveUpAsync(notification, result.Error ?? "delivery failed", now, cancellationToken);
                break;
        }
    }

    private async Task<SendResult> SendAsync(
        IChannelAdapter adapter,
        Notification notification,
        ChannelMessage message,
        CancellationToken cancellationToken)
    {
        var recipient = new ChannelRecipient(
            notification.Recipient.UserId,
            notification.Recipient.Name,
            notification.Recipient.Email,
            notification.Recipient.Phone);

        try
        {
            return await adapter.SendAsync(recipient, message, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A throwing adapter is treated as a temporary outage, never as a lost message.
            _logger.LogWarning(exception, "Channel {Channel} threw while sending notification {NotificationId}", adapter.Channel, notification.Id);

            return SendResult.Retry(exception.Message);
        }
    }

    private async Task GiveUpAsync(Notification notification, string error, DateTime now, CancellationToken cancellationToken)
    {
        notification.MarkDead(error, now);
        await _timeline.AppendAsync(notification, AlertEventKinds.NotificationFailed, now, cancellationToken);

        var next = _fallback.Next(new Channel(notification.Channel), _options.Fallbacks);
        if (next is null)
        {
            return;
        }

        var id = NotificationId.For(notification.AlertId, notification.UserId, next.Name, $"fallback:{notification.Id}");
        if (await _notifications.ExistsAsync(id.Value, cancellationToken))
        {
            return;
        }

        var fallback = Notification.Create(
            id,
            notification.AlertId,
            notification.IncidentId,
            notification.GroupKey,
            notification.Recipient,
            next,
            notification.ChangeId,
            notification.EscalationStep,
            $"fallback: {notification.Channel} failed ({error})",
            notification.Content,
            now,
            now);
        await _notifications.AddAsync(fallback, cancellationToken);
    }

    // Only a push channel counts against the limit, so only then is the person's counter locked.
    // A quiet-hours downgrade only ever makes a channel quieter, so the planned channel decides.
    private async Task<RateCounter?> LockCounterAsync(Recipient recipient, string channel, CancellationToken cancellationToken)
    {
        if (recipient.MaxPushPerHour is null || !new Channel(channel).IsPush)
        {
            return null;
        }

        return await _rateCounters.LockAsync(recipient.UserId, _clock.UtcNow, cancellationToken);
    }

    private TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Min(_options.MaxBackoffSeconds, _options.BaseBackoffSeconds * Math.Pow(2, attempt - 1));

        return TimeSpan.FromSeconds(seconds);
    }
}
