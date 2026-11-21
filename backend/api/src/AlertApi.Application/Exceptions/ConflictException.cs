namespace AlertApi.Application.Exceptions;

public abstract class ConflictException : AppException
{
    protected ConflictException(string details, Exception? innerException = null)
        : base(409, "Conflict", details, innerException)
    {
    }
}
