namespace AlertApi.Application.Exceptions;

public abstract class AppException : Exception
{
    protected AppException(int statusCode, string title, string details, Exception? innerException = null)
        : base(details, innerException)
    {
        StatusCode = statusCode;
        Title = title;
        Details = details;
    }

    public int StatusCode { get; }

    public string Title { get; }

    public string Details { get; }
}
