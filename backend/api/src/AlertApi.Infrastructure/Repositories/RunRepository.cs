using Microsoft.EntityFrameworkCore;

using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;
using AlertApi.Infrastructure.Data;

namespace AlertApi.Infrastructure.Repositories;

public sealed class RunRepository : IRunRepository
{
    // One statement, so a replica that loses the race for a row skips it instead of waiting. RETURNING gives the row back.
    private const string ClaimReadySql = """
        UPDATE action_runs
        SET status = 'running', attempt = attempt + 1, lease_until = {0}, started_at = {1}
        WHERE id = (
            SELECT node.id FROM action_runs node
            JOIN runbook_runs run ON run.id = node.run_id
            WHERE node.status = 'ready' AND (node.not_before IS NULL OR node.not_before <= {1}) AND run.status = 'running'
            ORDER BY node.not_before NULLS FIRST
            LIMIT 1
            FOR UPDATE OF node SKIP LOCKED)
        RETURNING *
        """;

    private const string ClaimExpiredSql = """
        UPDATE action_runs
        SET lease_until = {0}
        WHERE id = (
            SELECT id FROM action_runs
            WHERE status = 'running' AND lease_until < {1}
            ORDER BY lease_until
            LIMIT 1
            FOR UPDATE SKIP LOCKED)
        RETURNING *
        """;

    private const string ClaimDueScheduledSql = """
        UPDATE runbook_runs
        SET status = 'running', started_at = {0}
        WHERE id = (
            SELECT id FROM runbook_runs
            WHERE status = 'scheduled' AND start_at <= {0}
            ORDER BY start_at
            LIMIT 1
            FOR UPDATE SKIP LOCKED)
        RETURNING *
        """;

    private const string LockRunSql = "SELECT id FROM runbook_runs WHERE id = {0} FOR UPDATE";

    private readonly ApiDbContext _context;

    public RunRepository(ApiDbContext context)
    {
        _context = context;
    }

    public async Task<RunbookRun?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _context.Runs.FirstOrDefaultAsync(run => run.Id == id, cancellationToken);
    }

    public async Task<RunbookRun?> GetLatestByAlertIdAsync(string alertId, CancellationToken cancellationToken = default)
    {
        return await _context.Runs
            .AsNoTracking()
            .Where(run => run.AlertId == alertId)
            .OrderByDescending(run => run.CreatedAt)
            .ThenByDescending(run => run.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> HasLiveRunAsync(string alertId, CancellationToken cancellationToken = default)
    {
        return await _context.Runs.AnyAsync(
            run => run.AlertId == alertId && (run.Status == RunbookRun.Scheduled || run.Status == RunbookRun.Running),
            cancellationToken);
    }

    public async Task<RunbookRun?> GetLiveByAlertIdAsync(string alertId, CancellationToken cancellationToken = default)
    {
        return await _context.Runs.FirstOrDefaultAsync(
            run => run.AlertId == alertId && (run.Status == RunbookRun.Scheduled || run.Status == RunbookRun.Running),
            cancellationToken);
    }

    public async Task<RunbookRun?> ClaimDueScheduledAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var claimed = await _context.Runs
            .FromSqlRaw(ClaimDueScheduledSql, now)
            .ToListAsync(cancellationToken);

        return claimed.FirstOrDefault();
    }

    public async Task<IReadOnlyList<ActionRun>> GetNodesAsync(string runId, CancellationToken cancellationToken = default)
    {
        return await _context.ActionRuns
            .Where(node => node.RunId == runId)
            .OrderBy(node => node.StepIndex)
            .ToListAsync(cancellationToken);
    }

    public async Task<string?> GetApproverAsync(string runId, CancellationToken cancellationToken = default)
    {
        return await _context.Runs
            .AsNoTracking()
            .Where(run => run.Id == runId)
            .Select(run => run.ApprovedBy)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<ActionRun?> ClaimReadyAsync(DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        var claimed = await _context.ActionRuns
            .FromSqlRaw(ClaimReadySql, now + lease, now)
            .ToListAsync(cancellationToken);

        return claimed.FirstOrDefault();
    }

    public async Task<ActionRun?> ClaimExpiredAsync(DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        var claimed = await _context.ActionRuns
            .FromSqlRaw(ClaimExpiredSql, now + lease, now)
            .ToListAsync(cancellationToken);

        return claimed.FirstOrDefault();
    }

    public async Task LockAsync(string runId, CancellationToken cancellationToken = default)
    {
        await _context.Database.ExecuteSqlRawAsync(LockRunSql, [runId], cancellationToken);
    }

    public async Task AddAsync(RunbookRun run, IEnumerable<ActionRun> nodes, CancellationToken cancellationToken = default)
    {
        await _context.Runs.AddAsync(run, cancellationToken);
        await _context.ActionRuns.AddRangeAsync(nodes, cancellationToken);
    }
}
