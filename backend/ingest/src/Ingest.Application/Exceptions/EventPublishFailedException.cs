namespace Ingest.Application.Exceptions;

public sealed class EventPublishFailedException : ServiceUnavailableException
{
    public EventPublishFailedException(Exception innerException)
        : base("Kafka did not confirm the event. Retry later.", innerException)
    {
    }
}
