using AlertApi.Domain.Models;

namespace AlertApi.Domain.Repositories;

public interface IActionTargetRepository
{
    Task<IReadOnlyList<ActionTarget>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<ActionTarget?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task AddAsync(ActionTarget target, CancellationToken cancellationToken = default);
}
