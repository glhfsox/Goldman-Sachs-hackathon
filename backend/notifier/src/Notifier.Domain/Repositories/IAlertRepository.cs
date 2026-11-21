using Notifier.Domain.Models;
using Notifier.Domain.ValueObjects;

namespace Notifier.Domain.Repositories;

public interface IAlertRepository
{
    // Tracked: changes to the returned copies are saved.
    Task<IReadOnlyList<Alert>> GetByIdsAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default);

    Task<AlertStatus?> GetStatusAsync(string id, CancellationToken cancellationToken = default);

    Task AddAsync(Alert alert, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
