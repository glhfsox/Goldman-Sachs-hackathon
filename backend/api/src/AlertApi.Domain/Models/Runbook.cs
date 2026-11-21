namespace AlertApi.Domain.Models;

// A team's runbook (contracts/runbooks.md): the source as written and its canonical graph once converted.
public class Runbook
{
    public const string Draft = "draft";
    public const string Active = "active";

    private Runbook()
    {
    }

    public string Id { get; private set; } = null!;

    public string TeamId { get; private set; } = null!;

    public string Title { get; private set; } = null!;

    public string Format { get; private set; } = null!;

    public string Source { get; private set; } = null!;

    public string? Canonical { get; private set; }

    public string Status { get; private set; } = null!;

    public int Version { get; private set; }

    // A person accepted this version; automatic start of a run that changes something needs it.
    public string? AcceptedBy { get; private set; }

    public DateTimeOffset? AcceptedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Runbook Create(string id, string teamId, string title, string format, string source, DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        return new Runbook
        {
            Id = id,
            TeamId = teamId,
            Title = title,
            Format = format,
            Source = source,
            Status = Draft,
            Version = 1,
            UpdatedAt = updatedAt
        };
    }

    // A new canonical form is a new version: an acceptance of the old graph does not carry over.
    public void SetCanonical(string canonical, DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonical);

        Canonical = canonical;
        Version++;
        AcceptedBy = null;
        AcceptedAt = null;
        UpdatedAt = updatedAt;
    }

    public void SetStatus(string status, DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        Status = status;
        UpdatedAt = updatedAt;
    }

    public void Accept(string userId, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        AcceptedBy = userId;
        AcceptedAt = at;
    }
}
