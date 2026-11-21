using Microsoft.EntityFrameworkCore;

using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;
using AlertApi.Infrastructure.Data;

namespace AlertApi.Infrastructure.Repositories;

public sealed class RunbookRepository : IRunbookRepository
{
    private readonly ApiDbContext _context;

    public RunbookRepository(ApiDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<Runbook>> GetAllAsync(string? teamId, CancellationToken cancellationToken = default)
    {
        var query = _context.Runbooks.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(teamId))
        {
            query = query.Where(runbook => runbook.TeamId == teamId);
        }

        return await query.OrderBy(runbook => runbook.Id).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Runbook>> GetActiveByTeamAsync(string teamId, CancellationToken cancellationToken = default)
    {
        var runbooks = await _context.Runbooks
            .AsNoTracking()
            .Where(runbook => runbook.TeamId == teamId && runbook.Status == Runbook.Active && runbook.Canonical != null)
            .ToListAsync(cancellationToken);

        // Ordered here, not by the database, so the order does not depend on its collation.
        return runbooks.OrderBy(runbook => runbook.Id, StringComparer.Ordinal).ToList();
    }

    public async Task<Runbook?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _context.Runbooks.FirstOrDefaultAsync(runbook => runbook.Id == id, cancellationToken);
    }

    public async Task<bool> ExistsAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _context.Runbooks.AnyAsync(runbook => runbook.Id == id, cancellationToken);
    }

    public async Task AddAsync(Runbook runbook, CancellationToken cancellationToken = default)
    {
        await _context.Runbooks.AddAsync(runbook, cancellationToken);
    }
}
