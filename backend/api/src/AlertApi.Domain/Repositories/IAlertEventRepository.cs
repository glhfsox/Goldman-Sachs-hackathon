using AlertApi.Domain.Models;

namespace AlertApi.Domain.Repositories;

public interface IAlertEventRepository
{
    Task<bool> ExistsAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AlertEvent>> GetByAlertIdAsync(string alertId, CancellationToken cancellationToken = default);

    Task AddAsync(AlertEvent alertEvent, CancellationToken cancellationToken = default);
}
