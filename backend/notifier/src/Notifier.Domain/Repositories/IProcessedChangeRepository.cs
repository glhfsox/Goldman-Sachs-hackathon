using Notifier.Domain.Models;

namespace Notifier.Domain.Repositories;

public interface IProcessedChangeRepository
{
    // Of the given change ids, the ones already processed.
    Task<IReadOnlySet<string>> GetExistingAsync(IReadOnlyCollection<string> changeIds, CancellationToken cancellationToken = default);

    Task AddAsync(ProcessedChange change, CancellationToken cancellationToken = default);

    // Forgets changes processed before `before`: a redelivery that old is not expected any more.
    Task<int> DeleteBeforeAsync(DateTime before, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
