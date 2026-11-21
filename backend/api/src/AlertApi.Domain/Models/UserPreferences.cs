namespace AlertApi.Domain.Models;

public class UserPreferences
{
    private UserPreferences()
    {
    }

    public string UserId { get; private set; } = null!;

    // The whole object of contracts/rules.md section 5, stored as sent.
    public string Body { get; private set; } = null!;

    public static UserPreferences Create(string userId, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var preferences = new UserPreferences { UserId = userId };
        preferences.Replace(body);

        return preferences;
    }

    public void Replace(string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        Body = body;
    }
}
