namespace Notifier.Domain.Models;

// Read-only copy of the api's rule, fed by config.changes; Body is the Rule object of contracts/rules.md §4.
public class Rule
{
    private Rule()
    {
    }

    public string Id { get; private set; } = null!;

    public string TeamId { get; private set; } = null!;

    public bool Enabled { get; private set; }

    public string Body { get; private set; } = null!;

    public DateTime UpdatedAt { get; private set; }

    public static Rule Create(string id, string? teamId, bool enabled, string body, DateTime updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        return new Rule
        {
            Id = id,
            TeamId = teamId ?? string.Empty,
            Enabled = enabled,
            Body = body,
            UpdatedAt = updatedAt
        };
    }

    public void Update(string? teamId, bool enabled, string body, DateTime updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        if (updatedAt < UpdatedAt)
        {
            return;
        }

        TeamId = teamId ?? string.Empty;
        Enabled = enabled;
        Body = body;
        UpdatedAt = updatedAt;
    }
}
