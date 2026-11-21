namespace Notifier.Application.Services;

public interface ICleanupService
{
    // Deletes rows past their retention; returns how many.
    Task<int> CleanUpAsync(CancellationToken cancellationToken = default);
}
