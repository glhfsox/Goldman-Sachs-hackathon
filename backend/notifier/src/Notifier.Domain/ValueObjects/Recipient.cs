namespace Notifier.Domain.ValueObjects;

public sealed record Recipient
{
    public Recipient(
        string userId,
        string name,
        string? email,
        string? phone,
        bool onCall,
        IReadOnlyList<string> channels,
        string why,
        string? timeZone,
        QuietHours? quietHours,
        bool wakeMe,
        int? maxPushPerHour)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(channels);

        if (maxPushPerHour is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPushPerHour), "The push limit cannot be negative.");
        }

        UserId = userId;
        Name = string.IsNullOrWhiteSpace(name) ? userId : name;
        Email = email;
        Phone = phone;
        OnCall = onCall;
        Channels = channels;
        Why = why ?? string.Empty;
        TimeZone = timeZone;
        QuietHours = quietHours;
        WakeMe = wakeMe;
        MaxPushPerHour = maxPushPerHour;
    }

    public string UserId { get; }

    public string Name { get; }

    public string? Email { get; }

    public string? Phone { get; }

    public bool OnCall { get; }

    public IReadOnlyList<string> Channels { get; }

    public string Why { get; }

    public string? TimeZone { get; }

    public QuietHours? QuietHours { get; }

    public bool WakeMe { get; }

    public int? MaxPushPerHour { get; }
}
