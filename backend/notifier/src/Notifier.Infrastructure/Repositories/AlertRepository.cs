using Microsoft.EntityFrameworkCore;

using Notifier.Domain.Models;
using Notifier.Domain.Repositories;
using Notifier.Infrastructure.Data;

namespace Notifier.Infrastructure.Repositories;

public sealed class AlertRepository : IAlertRepository
{
    private readonly NotifierDbContext _context;

    public AlertRepository(NotifierDbContext context)
    {
        _context = context;
    }

    public async Task<Alert?> FindAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _context.Alerts.FirstOrDefaultAsync(alert => alert.Id == id, cancellationToken);
    }

    public async Task AddAsync(Alert alert, CancellationToken cancellationToken = default)
    {
        await _context.Alerts.AddAsync(alert, cancellationToken);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await _context.SaveChangesAsync(cancellationToken);
    }
}
