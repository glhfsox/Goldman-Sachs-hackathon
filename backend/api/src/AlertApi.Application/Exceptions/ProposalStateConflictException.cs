namespace AlertApi.Application.Exceptions;

public sealed class ProposalStateConflictException : ConflictException
{
    public ProposalStateConflictException(string details)
        : base(details)
    {
    }
}
