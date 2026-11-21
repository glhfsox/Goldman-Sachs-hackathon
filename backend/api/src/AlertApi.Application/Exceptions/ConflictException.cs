namespace AlertApi.Application.Exceptions;

public abstract class ConflictException : AppException
{
    protected ConflictException(string details)
        : base(409, "Conflict", details)
    {
    }
}
