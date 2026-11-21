using Microsoft.EntityFrameworkCore;

using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;
using AlertApi.Infrastructure.Data;

namespace AlertApi.Infrastructure.Repositories;

public sealed class RunRepository : IRunRepository
{
    private readonly ApiDbContext _context;

    public RunRepository(ApiDbContext context)
    {
        _context = context;
    }

    public async Task<RunbookRun?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _context.Runs.FirstOrDefaultAsync(run => run.Id == id, cancellationToken);
    }

    public async Task<bool> HasLiveRunAsync(string alertId, CancellationToken cancellationToken = default)
    {
        return await _context.Runs.AnyAsync(
            run => run.AlertId == alertId && (run.Status == RunbookRun.Scheduled || run.Status == RunbookRun.Running),
            cancellationToken);
    }

    public async Task<IReadOnlyList<ActionRun>> GetNodesAsync(string runId, CancellationToken cancellationToken = default)
    {
        return await _context.ActionRuns
            .Where(node => node.RunId == runId)
            .OrderBy(node => node.StepIndex)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(RunbookRun run, IEnumerable<ActionRun> nodes, CancellationToken cancellationToken = default)
    {
        await _context.Runs.AddAsync(run, cancellationToken);
        await _context.ActionRuns.AddRangeAsync(nodes, cancellationToken);
    }
}
