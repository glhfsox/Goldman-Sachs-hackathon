using Microsoft.EntityFrameworkCore;

using Notifier.Domain.Models;
using Notifier.Domain.Repositories;
using Notifier.Infrastructure.Data;

namespace Notifier.Infrastructure.Repositories;

public sealed class RateCounterRepository : IRateCounterRepository
{
    private readonly NotifierDbContext _context;

    public RateCounterRepository(NotifierDbContext context)
    {
        _context = context;
    }

    public async Task<RateCounter?> FindAsync(string userId, CancellationToken cancellationToken = default)
    {
        return await _context.RateCounters.FindAsync([userId], cancellationToken);
    }

    public async Task AddAsync(RateCounter counter, CancellationToken cancellationToken = default)
    {
        await _context.RateCounters.AddAsync(counter, cancellationToken);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await _context.SaveChangesAsync(cancellationToken);
    }
}
