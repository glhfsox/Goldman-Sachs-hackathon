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

    public async Task<IReadOnlyList<Notification>> GetOpenByAlertAsync(string alertId, CancellationToken cancellationToken = default)
    {
        return await _context.Notifications
            .Where(notification => notification.AlertId == alertId
                && (notification.Status == NotificationStatus.Pending || notification.Status == NotificationStatus.Failed))
            .ToListAsync(cancellationToken);
    }

    // A transaction-scoped advisory lock; NotifierDbContext.SaveChangesAsync commits the transaction and frees it.
    public async Task LockGroupAsync(string groupKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupKey);

        if (_context.Database.CurrentTransaction is null)
        {
            await _context.Database.BeginTransactionAsync(cancellationToken);
        }

        await _context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext({0}))", [groupKey], cancellationToken);
    }

    // UI rows do not count: seeing a card in the feed is not "being notified" about the incident.
    public async Task<IReadOnlySet<string>> GetNotifiedUsersAsync(
        IReadOnlyCollection<string> userIds,
        string groupKey,
        string excludedAlertId,
        DateTime since,
        CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0)
        {
            return new HashSet<string>();
        }

        var notified = await _context.Notifications.AsNoTracking()
            .Where(notification => userIds.Contains(notification.UserId)
                && notification.GroupKey == groupKey
                && notification.AlertId != excludedAlertId
                && notification.Channel != "ui"
                && notification.CreatedAt >= since
                && (notification.Status == NotificationStatus.Pending
                    || notification.Status == NotificationStatus.Sent
                    || notification.Status == NotificationStatus.Failed))
            .Select(notification => notification.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        return notified.ToHashSet();
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
