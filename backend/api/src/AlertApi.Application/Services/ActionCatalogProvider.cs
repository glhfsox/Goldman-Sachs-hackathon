using AlertApi.Application.Actions;
using AlertApi.Application.Interfaces;
using AlertApi.Domain.Repositories;

namespace AlertApi.Application.Services;

// Targets are read fresh per request: there are a few rows, and an edit must count at once on every replica.
public sealed class ActionCatalogProvider : IActionCatalogProvider
{
    private readonly IActionRegistry _registry;
    private readonly IActionTargetRepository _targets;

    public ActionCatalogProvider(IActionRegistry registry, IActionTargetRepository targets)
    {
        _registry = registry;
        _targets = targets;
    }

    public async Task<ActionCatalog> GetAsync(CancellationToken cancellationToken = default)
    {
        var targets = await _targets.GetAllAsync(cancellationToken);

        return new ActionCatalog(
            _registry.Descriptors,
            targets.Select(TargetDefinition.From).ToDictionary(target => target.Id));
    }
}
