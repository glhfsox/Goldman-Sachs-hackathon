namespace AlertApi.Application.Exceptions;

public sealed class InvalidRuleException : UnprocessableException
{
    public InvalidRuleException(IReadOnlyList<ProblemItem> problems)
        : base("invalid_rule", problems)
    {
    }
}
