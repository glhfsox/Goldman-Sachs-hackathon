using System.Text.Json;

namespace AlertApi.Application.Interfaces;

public interface IMappingService
{
    Task<IReadOnlyList<JsonElement>> GetAllAsync(CancellationToken cancellationToken = default);

    // Creates the mapping or replaces it: the ai-worker saves a proposal with the same call a person uses.
    // With a fingerprint, that schema sample becomes mapped in the same save.
    Task<JsonElement> SaveAsync(
        string id,
        JsonElement mapping,
        string? fingerprint,
        CancellationToken cancellationToken = default);
}
