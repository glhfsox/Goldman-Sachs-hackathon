namespace AlertApi.Application.Exceptions;

public sealed class RunNodeNotFoundException : NotFoundException
{
    public RunNodeNotFoundException(string runId, string node)
        : base($"Run '{runId}' has no step '{node}'.")
    {
    }
}
