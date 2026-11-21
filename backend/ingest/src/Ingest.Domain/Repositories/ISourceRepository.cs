using Ingest.Domain.Models;

namespace Ingest.Domain.Repositories;

public interface ISourceRepository
{
    Task<IReadOnlyList<Source>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Source?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task AddAsync(Source source, CancellationToken cancellationToken = default);
}
