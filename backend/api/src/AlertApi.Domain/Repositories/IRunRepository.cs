using AlertApi.Domain.Models;

namespace AlertApi.Domain.Repositories;

public interface IRunRepository
{
    Task<RunbookRun?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<RunbookRun?> GetLatestByAlertIdAsync(string alertId, CancellationToken cancellationToken = default);

    Task<bool> HasLiveRunAsync(string alertId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActionRun>> GetNodesAsync(string runId, CancellationToken cancellationToken = default);

    Task AddAsync(RunbookRun run, IEnumerable<ActionRun> nodes, CancellationToken cancellationToken = default);
}
