namespace AlertApi.Domain.Models;

public class Mapping
{
    private Mapping()
    {
    }

    public string Id { get; private set; } = null!;

    public string Status { get; private set; } = null!;

    public string Origin { get; private set; } = null!;

    public int Priority { get; private set; }

    // The whole mapping as JSON (contracts/rules.md section 3), stored as sent.
    public string Body { get; private set; } = null!;

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Mapping Create(string id, string status, string origin, int priority, string body, DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var mapping = new Mapping { Id = id };
        mapping.Update(status, origin, priority, body, updatedAt);

        return mapping;
    }

    public void Update(string status, string origin, int priority, string body, DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        Status = status;
        Origin = origin;
        Priority = priority;
        Body = body;
        UpdatedAt = updatedAt;
    }
}
