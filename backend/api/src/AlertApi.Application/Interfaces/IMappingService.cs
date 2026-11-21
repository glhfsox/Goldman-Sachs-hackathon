using System.Text.Json;

namespace AlertApi.Application.Interfaces;

public interface IMappingService
{
    Task<IReadOnlyList<JsonElement>> GetAllAsync(CancellationToken cancellationToken = default);

    // Creates the mapping or replaces it: the ai-worker saves a proposal with the same call a person uses.
    Task<JsonElement> SaveAsync(string id, JsonElement mapping, CancellationToken cancellationToken = default);
}
