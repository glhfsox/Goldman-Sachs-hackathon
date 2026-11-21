namespace AlertApi.Application.Exceptions;

// Another request changed the same row first (a concurrency token did not match); the caller reloads and retries.
public sealed class ConcurrentUpdateException : ConflictException
{
    public ConcurrentUpdateException(Exception innerException)
        : base("The record was changed by another request at the same time; reload it and try again.", innerException)
    {
    }
}
