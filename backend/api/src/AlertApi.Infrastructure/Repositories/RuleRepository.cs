using Microsoft.EntityFrameworkCore;

using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;
using AlertApi.Infrastructure.Data;

namespace AlertApi.Infrastructure.Repositories;

public sealed class RuleRepository : IRuleRepository
{
    private readonly ApiDbContext _context;

    public RuleRepository(ApiDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<Rule>> GetAllAsync(string? teamId, CancellationToken cancellationToken = default)
    {
        var query = _context.Rules.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(teamId))
        {
            query = query.Where(rule => rule.TeamId == teamId);
        }

        return await query.OrderBy(rule => rule.Id).ToListAsync(cancellationToken);
    }

    public async Task<Rule?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _context.Rules.FirstOrDefaultAsync(rule => rule.Id == id, cancellationToken);
    }

    public async Task AddAsync(Rule rule, CancellationToken cancellationToken = default)
    {
        await _context.Rules.AddAsync(rule, cancellationToken);
    }
}
