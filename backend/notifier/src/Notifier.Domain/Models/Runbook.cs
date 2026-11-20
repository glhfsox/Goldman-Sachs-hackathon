namespace Notifier.Domain.Models;

// Read-only copy of an api runbook so messages can carry its steps; the api owns and edits runbooks.
// Canonical is the canonical form of contracts/runbooks.md §1.
public class Runbook
{
    private const string ActiveStatus = "active";

    private Runbook()
    {
    }

    public string Id { get; private set; } = null!;

    public string? TeamId { get; private set; }

    public string Title { get; private set; } = null!;

    public string? Canonical { get; private set; }

    public string Status { get; private set; } = null!;

    public int Version { get; private set; }

    public DateTime UpdatedAt { get; private set; }

    public bool IsActive => Status == ActiveStatus && Canonical is not null;

    public static Runbook Create(
        string id,
        string? teamId,
        string? title,
        string? canonical,
        string status,
        int version,
        DateTime updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        return new Runbook
        {
            Id = id,
            TeamId = teamId,
            Title = string.IsNullOrWhiteSpace(title) ? id : title,
            Canonical = canonical,
            Status = status,
            Version = version,
            UpdatedAt = updatedAt
        };
    }

    public void Update(
        string? teamId,
        string? title,
        string? canonical,
        string status,
        int version,
        DateTime updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        if (updatedAt < UpdatedAt)
        {
            return;
        }

        TeamId = teamId;
        Title = string.IsNullOrWhiteSpace(title) ? Id : title;
        Canonical = canonical;
        Status = status;
        Version = version;
        UpdatedAt = updatedAt;
    }
}
