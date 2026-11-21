namespace Notifier.Application.Services;

public interface IDeliveryService
{
    // Leases the due rows and returns their ids; each one is then delivered on its own, possibly in parallel.
    Task<IReadOnlyList<string>> ClaimDueAsync(CancellationToken cancellationToken = default);

    // Delivers one claimed row and saves the outcome. Needs its own scope when called in parallel.
    Task DeliverAsync(string notificationId, CancellationToken cancellationToken = default);
}
