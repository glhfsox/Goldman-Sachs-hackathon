using Notifier.Domain.Models;

namespace Notifier.Domain.Repositories;

public interface INotificationRepository
{
    Task<bool> ExistsAsync(string id, CancellationToken cancellationToken = default);

    Task AddAsync(Notification notification, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Notification>> GetOpenByAlertAsync(string alertId, CancellationToken cancellationToken = default);

    Task<bool> HasDeliveredToGroupAsync(
        string userId,
        string groupKey,
        string excludedAlertId,
        DateTime since,
        CancellationToken cancellationToken = default);

    // Leases due rows by moving send_at to leaseUntil, so other replicas skip them while they are being sent.
    Task<IReadOnlyList<Notification>> ClaimDueAsync(
        DateTime now,
        int batchSize,
        DateTime leaseUntil,
        CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
