using Notifier.Domain.ValueObjects;

namespace Notifier.Domain.Tests;

public static class TestData
{
    public static readonly DateTime Now = new(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);

    public static Recipient Recipient(
        string userId = "marek",
        bool onCall = false,
        string[]? channels = null,
        QuietHours? quietHours = null,
        bool wakeMe = false,
        int? maxPushPerHour = null,
        string? timeZone = "Europe/Warsaw")
    {
        return new Recipient(
            userId,
            userId,
            $"{userId}@example.com",
            "+48000000000",
            onCall,
            channels ?? ["ui"],
            "rule test",
            timeZone,
            quietHours,
            wakeMe,
            maxPushPerHour);
    }

    // Anne's rule from the brief: SMS between 08:00 and 20:00, email otherwise.
    public static QuietHours EveningQuietHours() =>
        new(new TimeOnly(20, 0), new TimeOnly(8, 0), new Dictionary<string, string> { ["call"] = "sms", ["sms"] = "email" });

    public static NotificationContent Content(string severity = "critical") =>
        new("order-settlement failed at step settle", "Counterparty timeout", severity, "red", new Dictionary<string, string>(), null, []);
}
