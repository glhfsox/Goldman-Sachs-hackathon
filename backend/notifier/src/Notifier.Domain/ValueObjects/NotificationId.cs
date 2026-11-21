using System.Security.Cryptography;
using System.Text;

namespace Notifier.Domain.ValueObjects;

public sealed record NotificationId
{
    public NotificationId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        Value = value;
    }

    public string Value { get; }

    // Deterministic, so a redelivered Kafka record or a restarted worker produces the same id
    // and the insert is a no-op (storage.md: notifications.id is the idempotency key).
    public static NotificationId For(string alertId, string userId, string channel, string trigger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alertId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(trigger);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{alertId}|{userId}|{channel}|{trigger}"));

        return new NotificationId(Convert.ToHexStringLower(bytes)[..32]);
    }

    public override string ToString() => Value;
}
