using AlertApi.Domain.Models;

namespace AlertApi.Domain.Repositories;

public interface IMappingRepository
{
    Task<IReadOnlyList<Mapping>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Mapping?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task AddAsync(Mapping mapping, CancellationToken cancellationToken = default);
}
