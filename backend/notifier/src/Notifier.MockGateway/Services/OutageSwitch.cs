namespace Notifier.MockGateway.Services;

// Simulates a provider outage so retries, backoff and the fallback channel can be shown in the demo.
public sealed class OutageSwitch
{
    private readonly TimeProvider _time;
    private DateTimeOffset _until = DateTimeOffset.MinValue;

    public OutageSwitch(TimeProvider time)
    {
        _time = time;
    }

    public DateTimeOffset? ActiveUntil
    {
        get
        {
            var until = _until;

            return until > _time.GetUtcNow() ? until : null;
        }
    }

    public DateTimeOffset Start(TimeSpan duration)
    {
        _until = _time.GetUtcNow() + duration;

        return _until;
    }

    public void Stop()
    {
        _until = DateTimeOffset.MinValue;
    }
}
