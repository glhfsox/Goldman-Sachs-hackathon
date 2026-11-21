namespace AlertApi.Application.Exceptions;

public sealed class InvalidUserException : UnprocessableException
{
    public InvalidUserException(IReadOnlyList<ProblemItem> problems)
        : base("invalid_user", problems)
    {
    }
}
