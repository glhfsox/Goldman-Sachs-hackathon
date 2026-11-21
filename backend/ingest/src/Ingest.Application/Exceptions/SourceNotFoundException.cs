namespace Ingest.Application.Exceptions;

public sealed class SourceNotFoundException : NotFoundException
{
    public SourceNotFoundException(string sourceId)
        : base($"Source '{sourceId}' was not found or is disabled.")
    {
    }
}
