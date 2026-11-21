namespace Notifier.Domain.Models;

public class User
{
    private User()
    {
    }

    public string Id { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    public string? Email { get; private set; }

    public string? Phone { get; private set; }

    public DateTime UpdatedAt { get; private set; }

    public static User Create(string id, string? name, string? email, string? phone, DateTime updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        return new User
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? id : name,
            Email = email,
            Phone = phone,
            UpdatedAt = updatedAt
        };
    }

    public void Update(string? name, string? email, string? phone, DateTime updatedAt)
    {
        if (updatedAt < UpdatedAt)
        {
            return;
        }

        Name = string.IsNullOrWhiteSpace(name) ? Id : name;
        Email = email;
        Phone = phone;
        UpdatedAt = updatedAt;
    }
}
