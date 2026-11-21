namespace AlertApi.Domain.Models;

// Seeded by the AddReferenceData migration and read-only for now: the PoC has no login and no team editor.
public class Team
{
    private Team()
    {
    }

    public string Id { get; private set; } = null!;

    public string Name { get; private set; } = null!;
}
