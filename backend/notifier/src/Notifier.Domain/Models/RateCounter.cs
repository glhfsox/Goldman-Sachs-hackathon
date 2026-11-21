namespace Notifier.Domain.Models;

// One row per person: push notifications sent in the current hour, and how many were folded
// into the next "and N more" message once the limit was hit.
public class RateCounter
{
    private RateCounter()
    {
    }

    public string UserId { get; private set; } = null!;

    public DateTime WindowStart { get; private set; }

    public int PushCount { get; private set; }

    public int FoldedCount { get; private set; }

    public static RateCounter Create(string userId, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        return new RateCounter
        {
            UserId = userId,
            WindowStart = HourOf(now)
        };
    }

    public bool TryConsume(int limit, DateTime now)
    {
        Roll(now);
        if (PushCount >= limit)
        {
            FoldedCount++;

            return false;
        }

        PushCount++;

        return true;
    }

    public int TakeFolded()
    {
        var folded = FoldedCount;
        FoldedCount = 0;

        return folded;
    }

    // Folded messages survive the hour change: they are reported on the next push that goes out.
    private void Roll(DateTime now)
    {
        var hour = HourOf(now);
        if (hour > WindowStart)
        {
            WindowStart = hour;
            PushCount = 0;
        }
    }

    private static DateTime HourOf(DateTime at) => new(at.Year, at.Month, at.Day, at.Hour, 0, 0, DateTimeKind.Utc);
}
