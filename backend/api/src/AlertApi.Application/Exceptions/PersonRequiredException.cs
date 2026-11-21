namespace AlertApi.Application.Exceptions;

// Accepting a runbook or starting a run is a person's decision; a service name in X-User is not a user.
public sealed class PersonRequiredException : ForbiddenException
{
    public PersonRequiredException(string? actor, string what)
        : base($"Only a person can {what}; '{actor}' is not a known user.")
    {
    }
}
