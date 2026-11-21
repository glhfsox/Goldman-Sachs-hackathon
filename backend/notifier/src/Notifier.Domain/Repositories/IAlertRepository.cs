using Notifier.Domain.Models;
using Notifier.Domain.ValueObjects;

namespace Notifier.Domain.Repositories;

public interface IAlertRepository
{
    Task<Alert?> FindAsync(string id, CancellationToken cancellationToken = default);

    Task<AlertStatus?> GetStatusAsync(string id, CancellationToken cancellationToken = default);

    Task AddAsync(Alert alert, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
