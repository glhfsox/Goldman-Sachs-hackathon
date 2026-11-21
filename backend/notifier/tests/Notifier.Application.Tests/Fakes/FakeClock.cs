using Notifier.Application.Interfaces;

namespace Notifier.Application.Tests.Fakes;

public sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
}
