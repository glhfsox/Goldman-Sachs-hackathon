namespace AlertApi.Application.Interfaces;

// Disposing without a commit rolls back.
public interface IUnitOfWorkTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken = default);
}
