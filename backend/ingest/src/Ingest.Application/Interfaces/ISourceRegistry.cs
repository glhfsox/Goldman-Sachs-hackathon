using Ingest.Domain.Models;

namespace Ingest.Application.Interfaces;

// In-memory copy of the sources table, so a request never waits for the database.
public interface ISourceRegistry
{
    Source? Find(string id);

    IReadOnlyList<Source> GetEnabled();

    Task RefreshAsync(CancellationToken cancellationToken = default);
}
