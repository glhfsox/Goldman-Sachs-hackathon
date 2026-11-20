using Notifier.Domain.Models;

namespace Notifier.Domain.Repositories;

public interface IAlertRepository
{
    Task<Alert?> FindAsync(string id, CancellationToken cancellationToken = default);

    Task AddAsync(Alert alert, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
