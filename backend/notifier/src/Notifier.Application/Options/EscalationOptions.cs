namespace Notifier.Application.Options;

public sealed class EscalationOptions
{
    public const string SectionName = "Escalation";

    public double PushDelayMinutes { get; init; } = 5;

    public double OnCallCallDelayMinutes { get; init; } = 10;
}
