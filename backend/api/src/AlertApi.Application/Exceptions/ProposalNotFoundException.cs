namespace AlertApi.Application.Exceptions;

public sealed class ProposalNotFoundException : NotFoundException
{
    public ProposalNotFoundException(string id)
        : base($"Proposal with id '{id}' was not found.")
    {
    }
}
