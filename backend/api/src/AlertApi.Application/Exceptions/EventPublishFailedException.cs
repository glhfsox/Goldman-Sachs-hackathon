namespace AlertApi.Application.Exceptions;

public sealed class EventPublishFailedException : AppException
{
    public EventPublishFailedException(string topic, Exception innerException)
        : base(503, "Service Unavailable", $"The record for topic '{topic}' was not confirmed by Kafka.", innerException)
    {
    }
}
