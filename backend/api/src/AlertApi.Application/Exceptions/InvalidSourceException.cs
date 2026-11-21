namespace AlertApi.Application.Exceptions;

public sealed class InvalidSourceException : UnprocessableException
{
    public InvalidSourceException(IReadOnlyList<ProblemItem> problems)
        : base("invalid_source", problems)
    {
    }
}
