using Notifier.Domain.Models;
using Notifier.Domain.Services;
using Notifier.Domain.ValueObjects;

namespace Notifier.Domain.Tests;

public sealed class DeliveryGateTests
{
    // 20 Nov is winter time in Warsaw (UTC+1).
    private static readonly DateTime TenPmWarsaw = new(2026, 11, 20, 21, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NineAmWarsaw = new(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);

    private readonly DeliveryGate _gate = new();

    [Fact]
    public void Anne_critical_at_22_00_gets_email_instead_of_sms()
    {
        var anne = TestData.Recipient("anna", channels: ["sms"], quietHours: TestData.EveningQuietHours());

        var decision = _gate.Decide(anne, Channel.Sms, Severity.Critical, counter: null, TenPmWarsaw);

        Assert.False(decision.Suppressed);
        Assert.Equal(Channel.Email, decision.Channel);
    }

    [Fact]
    public void Anne_critical_at_09_00_gets_sms()
    {
        var anne = TestData.Recipient("anna", channels: ["sms"], quietHours: TestData.EveningQuietHours());

        var decision = _gate.Decide(anne, Channel.Sms, Severity.Critical, counter: null, NineAmWarsaw);

        Assert.Equal(Channel.Sms, decision.Channel);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void Wake_me_lets_critical_through_quiet_hours()
    {
        var recipient = TestData.Recipient(quietHours: TestData.EveningQuietHours(), wakeMe: true);

        var decision = _gate.Decide(recipient, Channel.Call, Severity.Critical, counter: null, TenPmWarsaw);

        Assert.Equal(Channel.Call, decision.Channel);
    }

    [Fact]
    public void Push_limit_suppresses_the_excess_and_folds_it()
    {
        var recipient = TestData.Recipient(maxPushPerHour: 2);
        var counter = RateCounter.Create(recipient.UserId, TestData.Now);

        var decisions = Enumerable.Range(0, 4)
            .Select(_ => _gate.Decide(recipient, Channel.Sms, Severity.Critical, counter, TestData.Now))
            .ToList();

        Assert.Equal(2, decisions.Count(decision => !decision.Suppressed));
        Assert.Equal(2, counter.TakeFolded());
    }

    [Fact]
    public void Push_limit_resets_next_hour()
    {
        var recipient = TestData.Recipient(maxPushPerHour: 1);
        var counter = RateCounter.Create(recipient.UserId, TestData.Now);
        _gate.Decide(recipient, Channel.Sms, Severity.Critical, counter, TestData.Now);

        var decision = _gate.Decide(recipient, Channel.Sms, Severity.Critical, counter, TestData.Now.AddHours(1));

        Assert.False(decision.Suppressed);
    }

    [Fact]
    public void Email_does_not_count_against_the_push_limit()
    {
        var recipient = TestData.Recipient(maxPushPerHour: 0);
        var counter = RateCounter.Create(recipient.UserId, TestData.Now);

        var decision = _gate.Decide(recipient, Channel.Email, Severity.Critical, counter, TestData.Now);

        Assert.False(decision.Suppressed);
    }
}
