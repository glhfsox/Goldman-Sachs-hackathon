using Notifier.Domain.Services;
using Notifier.Domain.ValueObjects;

namespace Notifier.Domain.Tests;

public sealed class EscalationLadderTests
{
    private static readonly EscalationPolicy Policy = new(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));

    private readonly EscalationLadder _ladder = new();

    [Fact]
    public void Critical_goes_to_every_channel_at_once()
    {
        var recipient = TestData.Recipient(onCall: true, channels: ["call", "sms", "ui"]);

        var plan = _ladder.Plan(recipient, Severity.Critical, Policy, TestData.Now);

        Assert.Equal(3, plan.Count);
        Assert.All(plan, delivery => Assert.Equal(TestData.Now, delivery.SendAt));
    }

    [Fact]
    public void Non_critical_shows_in_ui_first_and_pushes_after_the_delay()
    {
        var recipient = TestData.Recipient(channels: ["email", "ui"]);

        var plan = _ladder.Plan(recipient, Severity.Warning, Policy, TestData.Now);

        Assert.Equal(TestData.Now, plan.Single(delivery => delivery.Channel == Channel.Ui).SendAt);
        Assert.Equal(TestData.Now.AddMinutes(5), plan.Single(delivery => delivery.Channel == Channel.Email).SendAt);
    }

    [Fact]
    public void On_call_gets_a_call_when_nobody_acks_an_error()
    {
        var recipient = TestData.Recipient(onCall: true, channels: ["email"]);

        var plan = _ladder.Plan(recipient, Severity.Error, Policy, TestData.Now);

        var call = plan.Single(delivery => delivery.Channel == Channel.Call);
        Assert.Equal(TestData.Now.AddMinutes(15), call.SendAt);
        Assert.Equal(2, call.Step);
    }

    [Fact]
    public void Warnings_never_escalate_to_a_call()
    {
        var recipient = TestData.Recipient(onCall: true, channels: ["email"]);

        var plan = _ladder.Plan(recipient, Severity.Warning, Policy, TestData.Now);

        Assert.DoesNotContain(plan, delivery => delivery.Channel == Channel.Call);
    }

    [Fact]
    public void No_channels_means_ui_only()
    {
        var recipient = TestData.Recipient(channels: []);

        var plan = _ladder.Plan(recipient, Severity.Info, Policy, TestData.Now);

        Assert.Equal(Channel.Ui, Assert.Single(plan).Channel);
    }
}
