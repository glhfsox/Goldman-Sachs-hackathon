using AlertApi.Domain.Models;

namespace AlertApi.Domain.Repositories;

public interface ISchemaSampleRepository
{
    Task<IReadOnlyList<SchemaSample>> GetAllAsync(string? status, CancellationToken cancellationToken = default);

    Task<SchemaSample?> GetByFingerprintAsync(string fingerprint, CancellationToken cancellationToken = default);

    Task AddAsync(SchemaSample sample, CancellationToken cancellationToken = default);
}
