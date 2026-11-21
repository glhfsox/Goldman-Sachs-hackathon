using AlertApi.Domain.Models;

namespace AlertApi.Domain.Repositories;

public interface IRunbookStartRepository
{
    Task<RunbookStart?> GetAsync(string ruleId, string runbookId, CancellationToken cancellationToken = default);

    // Tracked, so a change made to the row is saved with the caller's next save.
    Task<RunbookStart?> GetTrackedAsync(string ruleId, string runbookId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RunbookStart>> GetAllAsync(string? ruleId, string? runbookId, CancellationToken cancellationToken = default);

    Task AddAsync(RunbookStart start, CancellationToken cancellationToken = default);
}
