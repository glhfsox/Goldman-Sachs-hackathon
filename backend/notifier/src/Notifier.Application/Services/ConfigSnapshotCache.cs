using Microsoft.Extensions.Options;

using Notifier.Application.Interfaces;
using Notifier.Application.Options;
using Notifier.Domain.Repositories;

namespace Notifier.Application.Services;

// config.changes has one partition, so only one replica applies a given record. That replica invalidates
// at once; the others see the change after at most ConfigCache:CacheSeconds.
public sealed class ConfigSnapshotCache : IConfigSnapshotCache
{
    private readonly SemaphoreSlim _reload = new(1, 1);
    private readonly IClock _clock;
    private readonly TimeSpan _maxAge;
    private ConfigSnapshot? _snapshot;
    private long _version;

    public ConfigSnapshotCache(IClock clock, IOptions<ConfigCacheOptions> options)
    {
        _clock = clock;
        _maxAge = TimeSpan.FromSeconds(options.Value.CacheSeconds);
    }

    public async Task<ConfigSnapshot> GetAsync(IConfigRepository repository, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);

        if (Fresh(Volatile.Read(ref _snapshot)) is { } snapshot)
        {
            return snapshot;
        }

        await _reload.WaitAsync(cancellationToken);
        try
        {
            if (Fresh(_snapshot) is { } loaded)
            {
                return loaded;
            }

            // Read the version before loading: an invalidation during the load makes this snapshot stale at once.
            var version = Interlocked.Read(ref _version);
            var copies = await repository.LoadAllAsync(cancellationToken);
            var reloaded = ConfigSnapshot.Build(copies, version, _clock.UtcNow);
            Volatile.Write(ref _snapshot, reloaded);

            return reloaded;
        }
        finally
        {
            _reload.Release();
        }
    }

    public void Invalidate()
    {
        Interlocked.Increment(ref _version);
    }

    private ConfigSnapshot? Fresh(ConfigSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.Version != Interlocked.Read(ref _version) || _clock.UtcNow - snapshot.LoadedAt >= _maxAge)
        {
            return null;
        }

        return snapshot;
    }
}
