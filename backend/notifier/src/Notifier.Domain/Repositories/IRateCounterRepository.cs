using Notifier.Domain.Models;

namespace Notifier.Domain.Repositories;

public interface IRateCounterRepository
{
    Task<RateCounter?> FindAsync(string userId, CancellationToken cancellationToken = default);

    Task AddAsync(RateCounter counter, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
