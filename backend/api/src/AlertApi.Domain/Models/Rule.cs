namespace AlertApi.Domain.Models;

public class Rule
{
    private Rule()
    {
    }

    public string Id { get; private set; } = null!;

    public string TeamId { get; private set; } = null!;

    public bool Enabled { get; private set; }

    // The whole rule as JSON (contracts/rules.md section 4): the api stores it as sent and never reads inside.
    public string Body { get; private set; } = null!;

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Rule Create(string id, string teamId, bool enabled, string body, DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var rule = new Rule { Id = id };
        rule.Update(teamId, enabled, body, updatedAt);

        return rule;
    }

    public void Update(string teamId, bool enabled, string body, DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        TeamId = teamId;
        Enabled = enabled;
        Body = body;
        UpdatedAt = updatedAt;
    }
}
