namespace AlertApi.Domain.Models;

public class User
{
    private User()
    {
    }

    public string Id { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    public string Email { get; private set; } = null!;

    public string Phone { get; private set; } = null!;
}
