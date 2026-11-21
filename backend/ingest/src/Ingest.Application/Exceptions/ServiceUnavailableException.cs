namespace Ingest.Application.Exceptions;

public abstract class ServiceUnavailableException : AppException
{
    protected ServiceUnavailableException(string details, Exception? innerException = null)
        : base(503, "Service Unavailable", details, innerException)
    {
    }
}
