namespace Ingest.Application.Exceptions;

public sealed class EventTooLargeException : PayloadTooLargeException
{
    public EventTooLargeException(int limitBytes)
        : base($"The request body is larger than {limitBytes} bytes.")
    {
    }
}
