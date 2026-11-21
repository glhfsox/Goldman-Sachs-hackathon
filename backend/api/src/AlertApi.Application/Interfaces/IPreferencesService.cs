using System.Text.Json;

namespace AlertApi.Application.Interfaces;

public interface IPreferencesService
{
    Task<JsonElement> GetAsync(string userId, CancellationToken cancellationToken = default);

    Task<JsonElement> SaveAsync(string userId, JsonElement preferences, CancellationToken cancellationToken = default);
}
