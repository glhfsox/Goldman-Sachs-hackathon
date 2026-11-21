using AlertApi.Domain.Models;

namespace AlertApi.Domain.Repositories;

public interface IPreferencesRepository
{
    Task<UserPreferences?> GetByUserIdAsync(string userId, CancellationToken cancellationToken = default);

    Task AddAsync(UserPreferences preferences, CancellationToken cancellationToken = default);
}
