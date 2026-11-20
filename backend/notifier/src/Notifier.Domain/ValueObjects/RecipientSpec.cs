namespace Notifier.Domain.ValueObjects;

// One entry of a rule's route.recipients: {"team": id}, {"team": id, "on_call": true} or {"user": id}.
public sealed record RecipientSpec
{
    public RecipientSpec(string? teamId, string? userId, bool onCallOnly)
    {
        if (string.IsNullOrWhiteSpace(teamId) == string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("A route recipient names exactly one of team or user.", nameof(teamId));
        }

        TeamId = teamId;
        UserId = userId;
        OnCallOnly = onCallOnly;
    }

    public string? TeamId { get; }

    public string? UserId { get; }

    public bool OnCallOnly { get; }
}
