namespace AlertApi.Application.Exceptions;

public sealed class InvalidRunRequestException : UnprocessableException
{
    public InvalidRunRequestException(IReadOnlyList<ProblemItem> problems)
        : base("invalid_run", problems)
    {
    }
}
