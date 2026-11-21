using Ingest.Infrastructure.Logging;

namespace Ingest.Tests.Logging;

public sealed class LogThrottleTests
{
    [Fact]
    public void TryAcquire_RepeatedKey_LogsOncePerIntervalWithSuppressedCount()
    {
        var clock = new ManualTimeProvider();
        var throttle = new LogThrottle(TimeSpan.FromSeconds(10), clock);

        Assert.True(throttle.TryAcquire("publish", out var first));
        Assert.Equal(0, first);

        for (var i = 0; i < 5; i++)
        {
            Assert.False(throttle.TryAcquire("publish", out _));
        }

        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.True(throttle.TryAcquire("publish", out var second));
        Assert.Equal(5, second);
        Assert.False(throttle.TryAcquire("publish", out _));
    }

    [Fact]
    public void TryAcquire_DifferentKeys_AreThrottledSeparately()
    {
        var throttle = new LogThrottle(TimeSpan.FromSeconds(10), new ManualTimeProvider());

        Assert.True(throttle.TryAcquire("read", out _));
        Assert.True(throttle.TryAcquire("commit", out _));
        Assert.False(throttle.TryAcquire("read", out _));
    }

    [Fact]
    public void TryAcquire_ZeroInterval_LogsEveryCall()
    {
        var throttle = new LogThrottle(TimeSpan.Zero, new ManualTimeProvider());

        Assert.True(throttle.TryAcquire("read", out _));
        Assert.True(throttle.TryAcquire("read", out _));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
