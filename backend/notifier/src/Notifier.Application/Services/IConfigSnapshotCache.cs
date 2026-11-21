using Notifier.Domain.Repositories;

namespace Notifier.Application.Services;

public interface IConfigSnapshotCache
{
    // The repository is the caller's scoped one; it is only used when the snapshot has to be reloaded.
    Task<ConfigSnapshot> GetAsync(IConfigRepository repository, CancellationToken cancellationToken = default);

    void Invalidate();
}
