namespace AlertApi.Application.Exceptions;

public sealed class InvalidRunbookException : UnprocessableException
{
    public InvalidRunbookException(IReadOnlyList<ProblemItem> problems)
        : base("invalid_runbook", problems)
    {
    }
}
