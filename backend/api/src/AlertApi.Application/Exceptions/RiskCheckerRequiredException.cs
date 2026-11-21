namespace AlertApi.Application.Exceptions;

// Only the ai-worker's judge answers a risk check; a person decides with approve or cancel.
public sealed class RiskCheckerRequiredException : ForbiddenException
{
    public RiskCheckerRequiredException(string? actor)
        : base($"Only the ai-worker can post a risk check; '{actor}' cannot.")
    {
    }
}
