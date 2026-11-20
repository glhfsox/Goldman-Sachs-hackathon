using Notifier.Domain.Models;
using Notifier.Domain.ValueObjects;

namespace Notifier.Domain.Services;

// Applied at send time, not at planning time: a message planned at 19:55 for 20:05 must respect quiet hours.
public sealed class DeliveryGate
{
    public GateDecision Decide(
        Recipient recipient,
        Channel channel,
        Severity severity,
        RateCounter? counter,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(severity);

        string? reason = null;
        var wakesThrough = recipient.WakeMe && severity.IsAtLeast(Severity.Critical);
        if (recipient.QuietHours is not null && !wakesThrough)
        {
            var localTime = TimeOnly.FromDateTime(ToLocal(utcNow, recipient.TimeZone));
            if (recipient.QuietHours.Contains(localTime))
            {
                var downgraded = recipient.QuietHours.DowngradeOf(channel);
                if (downgraded != channel)
                {
                    reason = $"quiet hours: {channel} downgraded to {downgraded}";
                    channel = downgraded;
                }
            }
        }

        if (channel.IsPush && recipient.MaxPushPerHour is int limit && counter is not null
            && !counter.TryConsume(limit, utcNow))
        {
            return GateDecision.Suppress(channel, $"rate_limit: more than {limit} push notifications per hour");
        }

        return GateDecision.Send(channel, reason);
    }

    // An unknown timezone falls back to UTC rather than blocking delivery.
    private static DateTime ToLocal(DateTime utcNow, string? timeZone)
    {
        if (string.IsNullOrWhiteSpace(timeZone) || !TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var zone))
        {
            return utcNow;
        }

        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), zone);
    }
}
