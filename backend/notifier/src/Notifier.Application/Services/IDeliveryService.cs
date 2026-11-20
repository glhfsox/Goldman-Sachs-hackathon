namespace Notifier.Application.Services;

public interface IDeliveryService
{
    Task<int> DeliverDueAsync(CancellationToken cancellationToken = default);
}
