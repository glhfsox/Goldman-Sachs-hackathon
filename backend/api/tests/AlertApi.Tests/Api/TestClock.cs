namespace AlertApi.Tests.Api;

// A clock a test moves by hand, so a cancel window passes without waiting.
public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now;

    public TestClock(DateTimeOffset now)
    {
        _now = now;
    }

    public void Advance(TimeSpan by)
    {
        _now += by;
    }

    public override DateTimeOffset GetUtcNow()
    {
        return _now;
    }
}
