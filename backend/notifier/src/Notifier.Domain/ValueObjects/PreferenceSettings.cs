namespace Notifier.Domain.ValueObjects;

public sealed record PreferenceSettings
{
    public PreferenceSettings(
        string? timeZone,
        IReadOnlyDictionary<string, IReadOnlyList<string>> bySeverity,
        QuietHours? quietHours,
        bool wakeMe,
        int? maxPushPerHour)
    {
        ArgumentNullException.ThrowIfNull(bySeverity);

        TimeZone = timeZone;
        BySeverity = bySeverity;
        QuietHours = quietHours;
        WakeMe = wakeMe;
        MaxPushPerHour = maxPushPerHour;
    }

    public string? TimeZone { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> BySeverity { get; }

    public QuietHours? QuietHours { get; }

    public bool WakeMe { get; }

    public int? MaxPushPerHour { get; }
}
