namespace AlertApi.Application.Exceptions;

public sealed class InvalidProposalException : UnprocessableException
{
    public InvalidProposalException(IReadOnlyList<ProblemItem> problems)
        : base("invalid_proposal", problems)
    {
    }
}
