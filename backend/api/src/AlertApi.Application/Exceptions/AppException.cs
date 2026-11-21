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

    // The "error" field of contracts/api.md; the UI switches on it, so it stays stable.
    public virtual string Code => Title.ToLowerInvariant().Replace(' ', '_');

    public virtual IReadOnlyList<ProblemItem> Problems => [];
}
