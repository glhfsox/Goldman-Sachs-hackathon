namespace Ingest.Infrastructure.Logging;

// Lets a repeating failure log once per interval per key. During an outage a retry loop can hit
// the same error thousands of times a second; one line with a count of the hidden ones is enough.
public sealed class LogThrottle
{
    public const int DefaultSeconds = 10;

    private readonly TimeSpan _interval;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = [];

    public LogThrottle(TimeSpan interval, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.Zero);

        _interval = interval;
        _timeProvider = timeProvider;
    }

    // True when the caller should log now; `suppressed` is how many calls with this key were skipped since the last line.
    public bool TryAcquire(string key, out int suppressed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var now = _timeProvider.GetTimestamp();

        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var entry) && _timeProvider.GetElapsedTime(entry.LastLogged, now) < _interval)
            {
                _entries[key] = entry with { Suppressed = entry.Suppressed + 1 };
                suppressed = 0;

                return false;
            }

            suppressed = entry?.Suppressed ?? 0;
            _entries[key] = new Entry(now, 0);

            return true;
        }
    }

    private sealed record Entry(long LastLogged, int Suppressed);
}
