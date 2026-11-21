using AlertApi.Domain.Models;

namespace AlertApi.Domain.Repositories;

public interface IRunRepository
{
    Task<RunbookRun?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<RunbookRun?> GetLatestByAlertIdAsync(string alertId, CancellationToken cancellationToken = default);

    Task<bool> HasLiveRunAsync(string alertId, CancellationToken cancellationToken = default);

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
