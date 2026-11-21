using Notifier.Domain.Models;
using Notifier.Domain.ValueObjects;

namespace Notifier.Domain.Repositories;

public interface INotificationRepository
{
    Task<Notification?> FindAsync(string id, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string id, CancellationToken cancellationToken = default);

    // One query for every id a plan would create: the ones returned already exist (redelivered change).
    Task<IReadOnlySet<string>> GetExistingIdsAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default);

    Task AddAsync(Notification notification, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Notification>> GetOpenByAlertsAsync(IReadOnlyCollection<string> alertIds, CancellationToken cancellationToken = default);

    // Serializes planning for these groups (incidents) until the next save, so two alerts of one incident planned
    // at the same time on other partitions or replicas still send one message per person. One statement for all
    // groups, taken in a fixed order so two batches never deadlock.
    Task LockGroupsAsync(IReadOnlyCollection<string> groupKeys, CancellationToken cancellationToken = default);

    // Non-UI rows that still count as "told" (pending, sent, failed) for these users and groups since `since`.
    Task<IReadOnlyList<NotifiedEntry>> GetNotifiedAsync(
        IReadOnlyCollection<string> userIds,
        IReadOnlyCollection<string> groupKeys,
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
