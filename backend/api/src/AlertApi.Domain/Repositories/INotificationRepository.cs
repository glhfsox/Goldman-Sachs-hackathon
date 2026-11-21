using AlertApi.Domain.Models;

namespace AlertApi.Domain.Repositories;

public interface INotificationRepository
{
    Task<Notification?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Notification>> GetByAlertIdAsync(string alertId, CancellationToken cancellationToken = default);

    Task AddAsync(Notification notification, CancellationToken cancellationToken = default);
}
