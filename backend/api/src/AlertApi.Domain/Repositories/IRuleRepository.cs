using AlertApi.Domain.Models;

namespace AlertApi.Domain.Repositories;

public interface IRuleRepository
{
    Task<IReadOnlyList<Rule>> GetAllAsync(string? teamId, CancellationToken cancellationToken = default);

    Task<Rule?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task AddAsync(Rule rule, CancellationToken cancellationToken = default);
}
