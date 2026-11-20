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
        RETURNING id, alert_id, incident_id, group_key, user_id, channel, change_id, escalation_step, reason,
                  status, send_at, attempts, recipient, content, rendered, provider_id, sent_at, error,
                  created_at, updated_at
        """;

    private readonly NotifierDbContext _context;

    public NotificationRepository(NotifierDbContext context)
    {
        _context = context;
    }

    public async Task<bool> ExistsAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _context.Notifications.AsNoTracking()
            .AnyAsync(notification => notification.Id == id, cancellationToken);
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

    // UI rows do not count: seeing a card in the feed is not "being notified" about the incident.
    public async Task<bool> HasDeliveredToGroupAsync(
        string userId,
        string groupKey,
        string excludedAlertId,
        DateTime since,
        CancellationToken cancellationToken = default)
    {
        return await _context.Notifications.AsNoTracking()
            .AnyAsync(
                notification => notification.UserId == userId
                    && notification.GroupKey == groupKey
                    && notification.AlertId != excludedAlertId
                    && notification.Channel != "ui"
                    && notification.CreatedAt >= since
                    && (notification.Status == NotificationStatus.Pending
                        || notification.Status == NotificationStatus.Sent
                        || notification.Status == NotificationStatus.Failed),
                cancellationToken);
    }

    public async Task<IReadOnlyList<Notification>> ClaimDueAsync(
        DateTime now,
        int batchSize,
        DateTime leaseUntil,
        CancellationToken cancellationToken = default)
    {
        return await _context.Notifications
            .FromSqlRaw(ClaimDueSql, now, batchSize, leaseUntil)
            .ToListAsync(cancellationToken);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await _context.SaveChangesAsync(cancellationToken);
    }
}
