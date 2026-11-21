namespace AlertApi.Application.Interfaces;

public interface IRunExecutor
{
    // Runs at most one node: a ready one, else one whose worker died. False when there was nothing to do.
    Task<bool> ExecuteNextAsync(CancellationToken cancellationToken = default);
}
