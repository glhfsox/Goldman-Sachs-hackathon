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
              AND ({1} = false OR risk <> 'auto' OR risk_check_verdict = 'clear')
            ORDER BY start_at
            LIMIT 1
            FOR UPDATE SKIP LOCKED)
        RETURNING *
        """;

    private const string HoldUnansweredSql = """
        UPDATE runbook_runs
        SET status = 'waiting_approval', risk_check_verdict = 'hold', risk_check_reasons = {1},
            risk_check_by = {2}, risk_check_at = {0}
        WHERE id = (
            SELECT id FROM runbook_runs
            WHERE status = 'scheduled' AND start_at <= {0} AND risk = 'auto' AND risk_check_verdict IS NULL
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
            run => run.AlertId == alertId && (run.Status == RunbookRun.Scheduled || run.Status == RunbookRun.WaitingApproval || run.Status == RunbookRun.Running),
            cancellationToken);
    }

    public async Task<RunbookRun?> GetLiveByAlertIdAsync(string alertId, CancellationToken cancellationToken = default)
    {
        return await _context.Runs.FirstOrDefaultAsync(
            run => run.AlertId == alertId && (run.Status == RunbookRun.Scheduled || run.Status == RunbookRun.WaitingApproval || run.Status == RunbookRun.Running),
            cancellationToken);
    }

    public async Task<RunbookRun?> ClaimDueScheduledAsync(DateTimeOffset now, bool requireClear, CancellationToken cancellationToken = default)
    {
        var claimed = await _context.Runs
            .FromSqlRaw(ClaimDueScheduledSql, now, requireClear)
            .ToListAsync(cancellationToken);

        return claimed.FirstOrDefault();
    }

    public async Task<RunbookRun?> HoldUnansweredAsync(DateTimeOffset now, string by, string reasonsJson, CancellationToken cancellationToken = default)
    {
        var held = await _context.Runs
            .FromSqlRaw(HoldUnansweredSql, now, reasonsJson, by)
            .ToListAsync(cancellationToken);

        return held.FirstOrDefault();
    }

    public async Task<IReadOnlyList<RunbookRun>> ListPendingRiskCheckAsync(int limit, CancellationToken cancellationToken = default)
    {
        return await _context.Runs
            .AsNoTracking()
            .Where(run => run.Status == RunbookRun.Scheduled && run.Risk == "auto" && run.RiskCheckVerdict == null)
            .OrderBy(run => run.StartAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RunbookRun>> ListFinishedSinceAsync(DateTimeOffset since, int limit, CancellationToken cancellationToken = default)
    {
        return await _context.Runs
            .AsNoTracking()
            .Where(run => (run.Status == RunbookRun.Done || run.Status == RunbookRun.Failed || run.Status == RunbookRun.Cancelled)
                && run.FinishedAt >= since)
            .OrderBy(run => run.FinishedAt)
            .ThenBy(run => run.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ActionRun>> GetNodesForRunsAsync(IReadOnlyCollection<string> runIds, CancellationToken cancellationToken = default)
    {
        return await _context.ActionRuns
            .AsNoTracking()
            .Where(node => runIds.Contains(node.RunId))
            .OrderBy(node => node.RunId)
            .ThenBy(node => node.StepIndex)
            .ToListAsync(cancellationToken);
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
