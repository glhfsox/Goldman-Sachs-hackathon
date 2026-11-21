namespace Notifier.Domain.Models;

public class Team
{
    private Team()
    {
    }

    public string Id { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    public DateTime UpdatedAt { get; private set; }

    public static Team Create(string id, string? name, DateTime updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        return new Team
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? id : name,
            UpdatedAt = updatedAt
        };
    }

    public void Update(string? name, DateTime updatedAt)
    {
        if (updatedAt < UpdatedAt)
        {
            return;
        }

        Name = string.IsNullOrWhiteSpace(name) ? Id : name;
        UpdatedAt = updatedAt;
    }
}
