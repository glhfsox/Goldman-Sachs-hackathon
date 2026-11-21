using AlertApi.Domain.Models;

namespace AlertApi.Domain.Repositories;

public interface IProposalRepository
{
    Task<Proposal?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Proposal>> GetAllAsync(string? status, int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Proposal>> GetOpenAsync(string alertId, string kind, CancellationToken cancellationToken = default);

    Task AddAsync(Proposal proposal, CancellationToken cancellationToken = default);
}
