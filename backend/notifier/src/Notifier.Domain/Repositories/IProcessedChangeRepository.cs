using Notifier.Domain.Models;

namespace Notifier.Domain.Repositories;

public interface IProcessedChangeRepository
{
    Task<bool> ExistsAsync(string changeId, CancellationToken cancellationToken = default);

    Task AddAsync(ProcessedChange change, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
