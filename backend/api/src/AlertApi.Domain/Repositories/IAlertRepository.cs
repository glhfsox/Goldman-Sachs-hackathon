using AlertApi.Domain.Models;

namespace AlertApi.Domain.Repositories;

public interface IAlertRepository
{
    Task<IReadOnlyList<Alert>> GetAllAsync(string? teamId, string? status, int limit, CancellationToken cancellationToken = default);

    Task<Alert?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task AddAsync(Alert alert, CancellationToken cancellationToken = default);
}
