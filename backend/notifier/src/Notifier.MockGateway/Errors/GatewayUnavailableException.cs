namespace Notifier.MockGateway.Errors;

public sealed class GatewayUnavailableException : Exception
{
    public GatewayUnavailableException(DateTimeOffset until)
        : base($"The mock gateway is in a simulated outage until {until:O}.")
    {
    }
}
