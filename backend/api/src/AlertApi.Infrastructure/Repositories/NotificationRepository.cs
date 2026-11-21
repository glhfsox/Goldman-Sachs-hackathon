using Microsoft.EntityFrameworkCore;

using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;
using AlertApi.Infrastructure.Data;

namespace AlertApi.Infrastructure.Repositories;

public sealed class NotificationRepository : INotificationRepository
{
    private readonly ApiDbContext _context;

    public NotificationRepository(ApiDbContext context)
    {
        _context = context;
    }

    public async Task<Notification?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _context.Notifications.FirstOrDefaultAsync(notification => notification.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<Notification>> GetByAlertIdAsync(string alertId, CancellationToken cancellationToken = default)
    {
        return await _context.Notifications
            .AsNoTracking()
            .Where(notification => notification.AlertId == alertId)
            .OrderBy(notification => notification.SendAt)
            .ThenBy(notification => notification.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        await _context.Notifications.AddAsync(notification, cancellationToken);
    }
}
