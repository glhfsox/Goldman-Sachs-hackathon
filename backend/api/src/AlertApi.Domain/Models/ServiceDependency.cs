namespace AlertApi.Domain.Models;

// "Service depends on DependsOn": the processor links alerts of the two into one incident.
public class ServiceDependency
{
    private ServiceDependency()
    {
    }

    public string Service { get; private set; } = null!;

    public string DependsOn { get; private set; } = null!;
}
