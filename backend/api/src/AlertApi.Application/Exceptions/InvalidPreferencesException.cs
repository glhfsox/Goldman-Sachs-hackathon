namespace AlertApi.Application.Exceptions;

public sealed class InvalidPreferencesException : UnprocessableException
{
    public InvalidPreferencesException(IReadOnlyList<ProblemItem> problems)
        : base("invalid_preferences", problems)
    {
    }
}
