namespace AlertApi.Application.Exceptions;

public sealed class InvalidActionTargetException : UnprocessableException
{
    public InvalidActionTargetException(IReadOnlyList<ProblemItem> problems)
        : base("invalid_action_target", problems)
    {
    }
}
