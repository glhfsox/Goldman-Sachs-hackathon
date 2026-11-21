namespace AlertApi.Domain.Models;

public class Membership
{
    private Membership()
    {
    }

    public string TeamId { get; private set; } = null!;

    public string UserId { get; private set; } = null!;

    public bool OnCall { get; private set; }
}
