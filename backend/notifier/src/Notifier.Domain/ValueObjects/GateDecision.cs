namespace Notifier.Domain.ValueObjects;

public sealed record GateDecision(
    bool Suppressed,
    Channel Channel,
    string? Reason)
{
    public static GateDecision Send(Channel channel, string? reason) => new(false, channel, reason);

    public static GateDecision Suppress(Channel channel, string reason) => new(true, channel, reason);
}
