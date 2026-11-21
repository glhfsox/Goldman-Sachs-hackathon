namespace AlertApi.Application.Exceptions;

public abstract class ForbiddenException : AppException
{
    protected ForbiddenException(string details)
        : base(403, "Forbidden", details)
    {
    }
}
