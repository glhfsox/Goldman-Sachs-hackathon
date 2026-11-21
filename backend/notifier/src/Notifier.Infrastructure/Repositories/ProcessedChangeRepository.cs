using Microsoft.EntityFrameworkCore;

using Notifier.Domain.Models;
using Notifier.Domain.Repositories;
using Notifier.Infrastructure.Data;

namespace Notifier.Infrastructure.Repositories;

public sealed class ProcessedChangeRepository : IProcessedChangeRepository
{
    private readonly NotifierDbContext _context;

    public ProcessedChangeRepository(NotifierDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlySet<string>> GetExistingAsync(
        IReadOnlyCollection<string> changeIds,
        CancellationToken cancellationToken = default)
    {
        if (changeIds.Count == 0)
        {
            return new HashSet<string>();
        }

        var existing = await _context.ProcessedChanges.AsNoTracking()
            .Where(change => changeIds.Contains(change.ChangeId))
            .Select(change => change.ChangeId)
            .ToListAsync(cancellationToken);

        return existing.ToHashSet();
    }

    public async Task AddAsync(ProcessedChange change, CancellationToken cancellationToken = default)
    {
        await _context.ProcessedChanges.AddAsync(change, cancellationToken);
    }

    public async Task<int> DeleteBeforeAsync(DateTime before, CancellationToken cancellationToken = default)
    {
        return await _context.ProcessedChanges
            .Where(change => change.ProcessedAt < before)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await _context.SaveChangesAsync(cancellationToken);
    }
}
