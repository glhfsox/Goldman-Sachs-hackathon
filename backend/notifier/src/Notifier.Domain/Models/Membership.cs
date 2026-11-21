namespace Notifier.Domain.Models;

public class Membership
{
    private Membership()
    {
    }

    public string TeamId { get; private set; } = null!;

    public string UserId { get; private set; } = null!;

    public bool OnCall { get; private set; }

    public DateTime UpdatedAt { get; private set; }

    public static Membership Create(string teamId, string userId, bool onCall, DateTime updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        return new Membership
        {
            TeamId = teamId,
            UserId = userId,
            OnCall = onCall,
            UpdatedAt = updatedAt
        };
    }

    public void Update(bool onCall, DateTime updatedAt)
    {
        if (updatedAt < UpdatedAt)
        {
            return;
        }

        OnCall = onCall;
        UpdatedAt = updatedAt;
    }
}
