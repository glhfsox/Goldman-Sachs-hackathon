namespace AlertApi.Application.Exceptions;

public sealed class RunbookStateConflictException : ConflictException
{
    public RunbookStateConflictException(string id, string reason)
        : base($"Runbook '{id}': {reason}.")
    {
    }
}
