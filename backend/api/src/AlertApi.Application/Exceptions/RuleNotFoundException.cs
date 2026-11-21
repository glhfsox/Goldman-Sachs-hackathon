namespace AlertApi.Application.Exceptions;

public sealed class RuleNotFoundException : NotFoundException
{
    public RuleNotFoundException(string id)
        : base($"Rule with id '{id}' was not found.")
    {
    }
}
