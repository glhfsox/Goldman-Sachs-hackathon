using AlertApi.Domain.Models;

namespace AlertApi.Domain.Repositories;

public interface IRunbookRepository
{
    Task<IReadOnlyList<Runbook>> GetAllAsync(string? teamId, CancellationToken cancellationToken = default);

    Task<Runbook?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string id, CancellationToken cancellationToken = default);

    Task AddAsync(Runbook runbook, CancellationToken cancellationToken = default);
}
