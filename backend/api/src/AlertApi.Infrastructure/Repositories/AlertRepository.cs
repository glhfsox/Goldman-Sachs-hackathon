using Microsoft.EntityFrameworkCore;

using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;
using AlertApi.Infrastructure.Data;

namespace AlertApi.Infrastructure.Repositories;

public sealed class AlertRepository : IAlertRepository
{
    private readonly ApiDbContext _context;

    public AlertRepository(ApiDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<Alert>> GetAllAsync(
        string? teamId,
        string? status,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Alerts.AsNoTracking();

        if (teamId is not null)
        {
            query = query.Where(alert => alert.TeamId == teamId);
        }

        if (status is not null)
        {
            query = query.Where(alert => alert.Status == status);
        }

        return await query
            .OrderByDescending(alert => alert.FirstSeen)
            .ThenBy(alert => alert.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<Alert?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _context.Alerts.FirstOrDefaultAsync(alert => alert.Id == id, cancellationToken);
    }

    public async Task AddAsync(Alert alert, CancellationToken cancellationToken = default)
    {
        await _context.Alerts.AddAsync(alert, cancellationToken);
    }
}
