using AlertApi.Application.Actions;

namespace AlertApi.Application.Interfaces;

public interface IActionCatalogProvider
{
    Task<ActionCatalog> GetAsync(CancellationToken cancellationToken = default);
}
