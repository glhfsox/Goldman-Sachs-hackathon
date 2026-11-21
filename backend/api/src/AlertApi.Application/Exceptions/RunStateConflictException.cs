namespace AlertApi.Application.Exceptions;

public sealed class RunStateConflictException : ConflictException
{
    public RunStateConflictException(string id, string reason)
        : base($"Run '{id}': {reason}.")
    {
    }
}
