using Notifier.Application.Interfaces;

namespace Notifier.Infrastructure.Time;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
