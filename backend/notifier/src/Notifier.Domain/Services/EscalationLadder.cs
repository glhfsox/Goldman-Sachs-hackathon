using Notifier.Domain.ValueObjects;

namespace Notifier.Domain.Services;

// Decides what goes out now and what waits for a missing ack, so non-critical alerts
// reach a phone only when nobody looked at the UI first (acceptance criteria, section 5).
public sealed class EscalationLadder
{
    public IReadOnlyList<PlannedDelivery> Plan(
        Recipient recipient,
        Severity severity,
        EscalationPolicy policy,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(severity);
        ArgumentNullException.ThrowIfNull(policy);

        var channels = recipient.Channels
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => new Channel(name))
            .Distinct()
            .ToList();
        if (channels.Count == 0)
        {
            channels.Add(Channel.Ui);
        }

        var plan = new List<PlannedDelivery>();
        var critical = severity.IsAtLeast(Severity.Critical);
        foreach (var channel in channels)
        {
            if (critical || channel == Channel.Ui)
            {
                plan.Add(new PlannedDelivery(channel, now, 0, critical ? "critical, sent at once" : "ui first"));
            }
            else
            {
                plan.Add(new PlannedDelivery(
                    channel,
                    now + policy.PushDelay,
                    1,
                    $"ladder step 1: no ack after {policy.PushDelay.TotalMinutes:0} min"));
            }
        }

        if (recipient.OnCall && severity.IsAtLeast(Severity.Error) && !channels.Contains(Channel.Call))
        {
            var callAfter = critical ? policy.OnCallCallDelay : policy.PushDelay + policy.OnCallCallDelay;
            plan.Add(new PlannedDelivery(
                Channel.Call,
                now + callAfter,
                2,
                $"ladder step 2: on-call, no ack after {callAfter.TotalMinutes:0} min"));
        }

        return plan;
    }
}
