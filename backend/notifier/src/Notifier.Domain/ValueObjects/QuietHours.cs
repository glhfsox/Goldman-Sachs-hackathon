namespace Notifier.Domain.ValueObjects;

public sealed record QuietHours
{
    public QuietHours(
        TimeOnly start,
        TimeOnly end,
        IReadOnlyDictionary<string, string> downgrade)
    {
        ArgumentNullException.ThrowIfNull(downgrade);

        Start = start;
        End = end;
        Downgrade = downgrade;
    }

    public TimeOnly Start { get; }

    public TimeOnly End { get; }

    public IReadOnlyDictionary<string, string> Downgrade { get; }

    // IsBetween wraps past midnight when Start > End, which is the usual 20:00-08:00 shape.
    public bool Contains(TimeOnly localTime) => localTime.IsBetween(Start, End);

    public Channel DowngradeOf(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return Downgrade.TryGetValue(channel.Name, out var target) ? new Channel(target) : channel;
    }
}
