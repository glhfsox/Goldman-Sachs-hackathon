using Microsoft.EntityFrameworkCore;

using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;
using AlertApi.Infrastructure.Data;

namespace AlertApi.Infrastructure.Repositories;

public sealed class ActionTargetRepository : IActionTargetRepository
{
    private readonly ApiDbContext _context;

    public ActionTargetRepository(ApiDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<ActionTarget>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _context.ActionTargets.AsNoTracking().OrderBy(target => target.Id).ToListAsync(cancellationToken);
    }

    public async Task<ActionTarget?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _context.ActionTargets.FirstOrDefaultAsync(target => target.Id == id, cancellationToken);
    }

    public async Task AddAsync(ActionTarget target, CancellationToken cancellationToken = default)
    {
        await _context.ActionTargets.AddAsync(target, cancellationToken);
    }
}
