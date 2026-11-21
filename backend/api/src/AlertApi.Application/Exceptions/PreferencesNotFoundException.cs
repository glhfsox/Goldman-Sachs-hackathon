namespace AlertApi.Application.Exceptions;

public sealed class PreferencesNotFoundException : NotFoundException
{
    public PreferencesNotFoundException(string userId)
        : base($"Preferences of user '{userId}' were not found.")
    {
    }
}
