namespace Notifier.Application.Interfaces;

public interface IClock
{
    DateTime UtcNow { get; }
}
