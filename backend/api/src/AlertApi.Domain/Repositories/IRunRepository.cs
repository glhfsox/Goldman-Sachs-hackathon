using AlertApi.Domain.Models;

namespace AlertApi.Domain.Repositories;

public interface IRunRepository
{
    Task<RunbookRun?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<RunbookRun?> GetLatestByAlertIdAsync(string alertId, CancellationToken cancellationToken = default);

    Task<bool> HasLiveRunAsync(string alertId, CancellationToken cancellationToken = default);

    // The alert's scheduled or running run, tracked; null when it has none.
    Task<RunbookRun?> GetLiveByAlertIdAsync(string alertId, CancellationToken cancellationToken = default);

    // Starts the oldest scheduled run whose start time has passed; replicas never get the same run.
    // With `requireClear`, an `auto` run is only taken when the risk check cleared it.
    Task<RunbookRun?> ClaimDueScheduledAsync(DateTimeOffset now, bool requireClear, CancellationToken cancellationToken = default);

    // Holds the oldest `auto` run whose start time passed with no verdict (it becomes waiting_approval); replicas never get the same run.
    Task<RunbookRun?> HoldUnansweredAsync(DateTimeOffset now, string by, string reasonsJson, CancellationToken cancellationToken = default);

    // Scheduled `auto` runs without a verdict, oldest start first.
    Task<IReadOnlyList<RunbookRun>> ListPendingRiskCheckAsync(int limit, CancellationToken cancellationToken = default);

    // Done, failed or cancelled runs finished at or after `since`, oldest first.
    Task<IReadOnlyList<RunbookRun>> ListFinishedSinceAsync(DateTimeOffset since, int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActionRun>> GetNodesForRunsAsync(IReadOnlyCollection<string> runIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActionRun>> GetNodesAsync(string runId, CancellationToken cancellationToken = default);

    // Read without tracking: the run is loaded again, under its lock, when a step completes.
    Task<string?> GetApproverAsync(string runId, CancellationToken cancellationToken = default);

    // Takes the oldest ready node of a running run for `lease`, counting the call; null when none is due.
    // Replicas never get the same node.
    Task<ActionRun?> ClaimReadyAsync(DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken = default);

    // Takes a running node whose lease ran out (its worker died), keeping the attempt count.
    Task<ActionRun?> ClaimExpiredAsync(DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken = default);

    // Holds a lock on the run until the transaction ends, so two nodes finishing together plan one after the other.
    Task LockAsync(string runId, CancellationToken cancellationToken = default);

    Task AddAsync(RunbookRun run, IEnumerable<ActionRun> nodes, CancellationToken cancellationToken = default);
}
