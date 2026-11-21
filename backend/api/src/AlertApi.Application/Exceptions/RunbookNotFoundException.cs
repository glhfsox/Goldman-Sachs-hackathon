namespace AlertApi.Application.Exceptions;

public sealed class RunbookNotFoundException : NotFoundException
{
    public RunbookNotFoundException(string id)
        : base($"Runbook with id '{id}' was not found.")
    {
    }
}
