namespace AlertApi.Application.Exceptions;

public sealed class InvalidRunbookStartException : UnprocessableException
{
    public InvalidRunbookStartException(IReadOnlyList<ProblemItem> problems)
        : base("invalid_runbook_start", problems)
    {
    }
}
