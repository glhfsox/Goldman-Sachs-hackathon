using System.Collections.Frozen;

using Microsoft.Extensions.DependencyInjection;

using Ingest.Application.Interfaces;
using Ingest.Domain.Models;
using Ingest.Domain.Repositories;

namespace Ingest.Infrastructure.Registry;

public sealed class InMemorySourceRegistry : ISourceRegistry
{
    private readonly IServiceScopeFactory _scopeFactory;

    // Replaced as a whole on refresh, so readers never see a half-updated map.
    private volatile FrozenDictionary<string, Source> _sources = FrozenDictionary<string, Source>.Empty;

    public InMemorySourceRegistry(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public Source? Find(string id)
    {
        return _sources.GetValueOrDefault(id);
    }

    public IReadOnlyList<Source> GetEnabled()
    {
        return _sources.Values.Where(source => source.Enabled).ToList();
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISourceRepository>();

        var sources = await repository.GetAllAsync(cancellationToken);

        _sources = sources.ToFrozenDictionary(source => source.Id);
    }
}
