using Microsoft.EntityFrameworkCore;

using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;
using AlertApi.Infrastructure.Data;

namespace AlertApi.Infrastructure.Repositories;

public sealed class RunbookStartRepository : IRunbookStartRepository
{
    private readonly ApiDbContext _context;

    public RunbookStartRepository(ApiDbContext context)
    {
        _context = context;
    }

    public async Task<RunbookStart?> GetAsync(string ruleId, string runbookId, CancellationToken cancellationToken = default)
    {
        return await _context.RunbookStarts
            .AsNoTracking()
            .FirstOrDefaultAsync(start => start.RuleId == ruleId && start.RunbookId == runbookId, cancellationToken);
    }

    public async Task<RunbookStart?> GetTrackedAsync(string ruleId, string runbookId, CancellationToken cancellationToken = default)
    {
        return await _context.RunbookStarts
            .FirstOrDefaultAsync(start => start.RuleId == ruleId && start.RunbookId == runbookId, cancellationToken);
    }

    public async Task<IReadOnlyList<RunbookStart>> GetAllAsync(string? ruleId, string? runbookId, CancellationToken cancellationToken = default)
    {
        var query = _context.RunbookStarts.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(ruleId))
        {
            query = query.Where(start => start.RuleId == ruleId);
        }

        if (!string.IsNullOrWhiteSpace(runbookId))
        {
            query = query.Where(start => start.RunbookId == runbookId);
        }

        return await query
            .OrderBy(start => start.RuleId)
            .ThenBy(start => start.RunbookId)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(RunbookStart start, CancellationToken cancellationToken = default)
    {
        await _context.RunbookStarts.AddAsync(start, cancellationToken);
    }
}
