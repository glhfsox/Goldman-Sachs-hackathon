using Microsoft.EntityFrameworkCore;

using Notifier.Domain.Models;
using Notifier.Domain.Repositories;
using Notifier.Domain.ValueObjects;
using Notifier.Infrastructure.Data;

namespace Notifier.Infrastructure.Repositories;

public sealed class NotificationRepository : INotificationRepository
{
    // SKIP LOCKED lets every replica claim a disjoint batch; moving send_at forward is the lease,
    // so a row whose worker died is picked up again once the lease runs out.
    private const string ClaimDueSql = """
        UPDATE notifications
        SET send_at = {2}
        WHERE id IN (
            SELECT id
            FROM notifications
            WHERE status IN ('pending', 'failed') AND send_at <= {0}
            ORDER BY send_at
            LIMIT {1}
            FOR UPDATE SKIP LOCKED)
        RETURNING id AS "Value"
        """;

    private readonly NotifierDbContext _context;

    public NotificationRepository(NotifierDbContext context)
    {
        _context = context;
    }

    public async Task<Notification?> FindAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _context.Notifications.FirstOrDefaultAsync(notification => notification.Id == id, cancellationToken);
    }

    public async Task<bool> ExistsAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _context.Notifications.AsNoTracking()
            .AnyAsync(notification => notification.Id == id, cancellationToken);
    }

    public async Task<IReadOnlySet<string>> GetExistingIdsAsync(
        IReadOnlyCollection<string> ids,
        CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return new HashSet<string>();
        }

        var existing = await _context.Notifications.AsNoTracking()
            .Where(notification => ids.Contains(notification.Id))
            .Select(notification => notification.Id)
            .ToListAsync(cancellationToken);

        return existing.ToHashSet();
    }

    public async Task AddAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        await _context.Notifications.AddAsync(notification, cancellationToken);
    }

    public async Task<IReadOnlyList<Notification>> GetOpenByAlertsAsync(
        IReadOnlyCollection<string> alertIds,
        CancellationToken cancellationToken = default)
    {
        if (alertIds.Count == 0)
        {
            return [];
        }

        return await _context.Notifications
            .Where(notification => alertIds.Contains(notification.AlertId)
                && (notification.Status == NotificationStatus.Pending || notification.Status == NotificationStatus.Failed))
            .ToListAsync(cancellationToken);
    }

    // Transaction-scoped advisory locks; NotifierDbContext.SaveChangesAsync commits the transaction and frees them.
    // Keys are sorted here and taken in array order (ORDER BY ordinality defers the volatile lock call past the sort).
    public async Task LockGroupsAsync(IReadOnlyCollection<string> groupKeys, CancellationToken cancellationToken = default)
    {
        var keys = groupKeys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (keys.Length == 0)
        {
            return;
        }

        if (_context.Database.CurrentTransaction is null)
        {
            await _context.Database.BeginTransactionAsync(cancellationToken);
        }

        await _context.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtext(locks.key)) FROM unnest({0}::text[]) WITH ORDINALITY AS locks(key, position) ORDER BY locks.position",
            new object[] { keys },
            cancellationToken);
    }

    // UI rows do not count: seeing a card in the feed is not "being notified" about the incident.
    public async Task<IReadOnlyList<NotifiedEntry>> GetNotifiedAsync(
        IReadOnlyCollection<string> userIds,
        IReadOnlyCollection<string> groupKeys,
        DateTime since,
        CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0 || groupKeys.Count == 0)
        {
            return [];
        }

        return await _context.Notifications.AsNoTracking()
            .Where(notification => userIds.Contains(notification.UserId)
                && groupKeys.Contains(notification.GroupKey)
                && notification.Channel != "ui"
                && notification.CreatedAt >= since
                && (notification.Status == NotificationStatus.Pending
                    || notification.Status == NotificationStatus.Sent
                    || notification.Status == NotificationStatus.Failed))
            .Select(notification => new NotifiedEntry(notification.GroupKey, notification.UserId, notification.AlertId, notification.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ClaimDueAsync(
        DateTime now,
        int batchSize,
        DateTime leaseUntil,
        CancellationToken cancellationToken = default)
    {
        return await _context.Database
            .SqlQueryRaw<string>(ClaimDueSql, now, batchSize, leaseUntil)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> DeleteFinishedBeforeAsync(DateTime before, CancellationToken cancellationToken = default)
    {
        return await _context.Notifications
            .Where(notification => notification.UpdatedAt < before
                && (notification.Status == NotificationStatus.Sent
                    || notification.Status == NotificationStatus.Dead
                    || notification.Status == NotificationStatus.Cancelled
                    || notification.Status == NotificationStatus.Suppressed))
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await _context.SaveChangesAsync(cancellationToken);
    }
}
