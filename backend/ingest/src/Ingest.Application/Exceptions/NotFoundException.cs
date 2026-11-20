namespace Ingest.Application.Exceptions;

public abstract class NotFoundException : AppException
{
    protected NotFoundException(string details)
        : base(404, "Not Found", details)
    {
    }
}
