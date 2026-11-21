namespace AlertApi.Application.Exceptions;

public sealed class RuleAlreadyExistsException : ConflictException
{
    public RuleAlreadyExistsException(string id)
        : base($"Rule with id '{id}' already exists.")
    {
    }
}
