using System.Text.Json;

namespace AlertApi.Application.Interfaces;

public interface IRuleService
{
    Task<IReadOnlyList<JsonElement>> GetAllAsync(string? teamId, CancellationToken cancellationToken = default);

    Task<JsonElement> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<JsonElement> CreateAsync(JsonElement rule, CancellationToken cancellationToken = default);

    Task<JsonElement> UpdateAsync(string id, JsonElement rule, CancellationToken cancellationToken = default);
}
