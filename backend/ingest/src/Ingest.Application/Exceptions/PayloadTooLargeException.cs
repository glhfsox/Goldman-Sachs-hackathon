namespace Ingest.Application.Exceptions;

public abstract class PayloadTooLargeException : AppException
{
    protected PayloadTooLargeException(string details)
        : base(413, "Payload Too Large", details)
    {
    }
}
