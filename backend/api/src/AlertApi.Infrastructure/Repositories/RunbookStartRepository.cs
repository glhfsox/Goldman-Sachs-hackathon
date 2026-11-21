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
}
