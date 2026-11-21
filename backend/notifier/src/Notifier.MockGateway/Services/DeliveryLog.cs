using System.Collections.Concurrent;

using Notifier.MockGateway.DTOs.Responses;
using Notifier.MockGateway.Errors;

namespace Notifier.MockGateway.Services;

public sealed class DeliveryLog
{
    private readonly ConcurrentDictionary<string, DeliveryResponse> _deliveries = new();
    private readonly OutageSwitch _outage;
    private readonly TimeProvider _time;
    private readonly ILogger<DeliveryLog> _logger;

    public DeliveryLog(
        OutageSwitch outage,
        TimeProvider time,
        ILogger<DeliveryLog> logger)
    {
        _outage = outage;
        _time = time;
        _logger = logger;
    }

    // Idempotent on (channel, notification id), like a real provider with an idempotency key:
    // a retried request returns the first delivery instead of texting the person twice.
    public DeliveryResponse Record(string channel, string notificationId, string to, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(notificationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);

        if (_outage.ActiveUntil is { } until)
        {
            throw new GatewayUnavailableException(until);
        }

        var isNew = false;
        var delivery = _deliveries.GetOrAdd($"{channel}:{notificationId}", _ =>
        {
            isNew = true;

            return new DeliveryResponse(
                Guid.NewGuid().ToString("N"),
                channel,
                notificationId,
                to,
                text,
                _time.GetUtcNow());
        });

        // The timestamp in this line is the end point of the ingest -> delivery latency measurement.
        if (isNew)
        {
            _logger.LogInformation(
                "{Channel} delivered at {ReceivedAt:O} to {To} (notification {NotificationId}): {Text}",
                channel.ToUpperInvariant(),
                delivery.ReceivedAt,
                to,
                notificationId,
                text);
        }

        return delivery;
    }

    public IReadOnlyList<DeliveryResponse> GetAll(string? channel, int limit)
    {
        var deliveries = _deliveries.Values.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(channel))
        {
            deliveries = deliveries.Where(delivery => delivery.Channel == channel);
        }

        return deliveries
            .OrderByDescending(delivery => delivery.ReceivedAt)
            .Take(limit)
            .ToList();
    }
}
