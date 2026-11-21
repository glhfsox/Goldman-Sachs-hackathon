using Notifier.Domain.Models;

namespace Notifier.Domain.Repositories;

public interface INotificationRepository
{
    Task<Notification?> FindAsync(string id, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string id, CancellationToken cancellationToken = default);

    // One query for every id a plan would create: the ones returned already exist (redelivered change).
    Task<IReadOnlySet<string>> GetExistingIdsAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default);

    Task AddAsync(Notification notification, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Notification>> GetOpenByAlertAsync(string alertId, CancellationToken cancellationToken = default);

    // Of the given users, those already told about this group (incident) through another alert since `since`.
    Task<IReadOnlySet<string>> GetNotifiedUsersAsync(
        IReadOnlyCollection<string> userIds,
        string groupKey,
        string excludedAlertId,
        DateTime since,
        CancellationToken cancellationToken = default);

    // Leases due rows by moving send_at to leaseUntil, so other replicas skip them while they are being sent.
    Task<IReadOnlyList<string>> ClaimDueAsync(
        DateTime now,
        int batchSize,
        DateTime leaseUntil,
        CancellationToken cancellationToken = default);

    // Deletes rows that reached a final status before `before`; returns how many.
    Task<int> DeleteFinishedBeforeAsync(DateTime before, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
