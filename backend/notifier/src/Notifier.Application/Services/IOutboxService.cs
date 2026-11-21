namespace Notifier.Application.Services;

public interface IOutboxService
{
    Task<int> DispatchAsync(CancellationToken cancellationToken = default);
}
