using Microsoft.EntityFrameworkCore;

using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;
using AlertApi.Infrastructure.Data;

namespace AlertApi.Infrastructure.Repositories;

public sealed class AlertEventRepository : IAlertEventRepository
{
    private readonly ApiDbContext _context;

    public AlertEventRepository(ApiDbContext context)
    {
        _context = context;
    }

    public async Task<bool> ExistsAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _context.AlertEvents.AnyAsync(alertEvent => alertEvent.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<AlertEvent>> GetByAlertIdAsync(string alertId, CancellationToken cancellationToken = default)
    {
        return await _context.AlertEvents
            .AsNoTracking()
            .Where(alertEvent => alertEvent.AlertId == alertId)
            .OrderBy(alertEvent => alertEvent.At)
            .ThenBy(alertEvent => alertEvent.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(AlertEvent alertEvent, CancellationToken cancellationToken = default)
    {
        await _context.AlertEvents.AddAsync(alertEvent, cancellationToken);
    }
}
