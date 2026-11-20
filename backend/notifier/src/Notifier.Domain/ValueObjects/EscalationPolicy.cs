namespace Notifier.Domain.ValueObjects;

public sealed record EscalationPolicy
{
    public EscalationPolicy(
        TimeSpan pushDelay,
        TimeSpan onCallCallDelay)
    {
        if (pushDelay < TimeSpan.Zero || onCallCallDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pushDelay), "Escalation delays cannot be negative.");
        }

        PushDelay = pushDelay;
        OnCallCallDelay = onCallCallDelay;
    }

    public TimeSpan PushDelay { get; }

    public TimeSpan OnCallCallDelay { get; }
}
