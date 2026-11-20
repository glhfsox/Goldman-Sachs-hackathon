namespace Notifier.Domain.Models;

// Copy of a person's channel preferences; Body is the Preferences object of contracts/rules.md §5.
public class UserPreferences
{
    private UserPreferences()
    {
    }

    public string UserId { get; private set; } = null!;

    public string Body { get; private set; } = null!;

    public DateTime UpdatedAt { get; private set; }

    public static UserPreferences Create(string userId, string body, DateTime updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        return new UserPreferences
        {
            UserId = userId,
            Body = body,
            UpdatedAt = updatedAt
        };
    }

    public void Update(string body, DateTime updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        if (updatedAt < UpdatedAt)
        {
            return;
        }

        Body = body;
        UpdatedAt = updatedAt;
    }
}
