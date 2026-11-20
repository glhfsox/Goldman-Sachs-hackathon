using Notifier.Application.Constants;
using Notifier.Application.DTOs.Channels;
using Notifier.Application.Options;
using Notifier.Application.Services;
using Notifier.Application.Tests.Fakes;
using Notifier.Domain.ValueObjects;

namespace Notifier.Application.Tests;

public sealed class DeliveryServiceTests
{
    [Fact]
    public async Task Sends_through_the_adapter_and_records_the_timeline()
    {
        var harness = new Harness();
        var sms = new FakeChannel("sms", SendResult.Sent("provider-1"));
        harness.Channels.Adapters["sms"] = sms;
        await harness.SeedRuleAsync("rule-1", ["sms"], "marek");
        await harness.AlertChanges.HandleAsync(Harness.Change(AlertChanges.Opened));

        await harness.Deliveries.DeliverDueAsync();

        var notification = Assert.Single(harness.Store.Notifications);
        Assert.Equal(NotificationStatus.Sent, notification.Status);
        Assert.Single(sms.Sent);
        Assert.Contains(harness.Store.Outbox, message =>
            message.Topic == Topics.AlertsTimeline && message.Payload.Contains(AlertEventKinds.NotificationSent));
    }

    [Fact]
    public async Task Retries_then_gives_up_and_falls_back_to_the_next_channel()
    {
        var harness = new Harness(new DeliveryOptions { MaxAttempts = 2 });
        harness.Channels.Adapters["sms"] = new FakeChannel("sms", SendResult.Retry("gateway down"));
        var email = new FakeChannel("email", SendResult.Sent("mail-1"));
        harness.Channels.Adapters["email"] = email;
        await harness.SeedRuleAsync("rule-1", ["sms"], "marek");
        await harness.AlertChanges.HandleAsync(Harness.Change(AlertChanges.Opened));

        await harness.Deliveries.DeliverDueAsync();
        harness.Clock.UtcNow = harness.Clock.UtcNow.AddMinutes(1);
        await harness.Deliveries.DeliverDueAsync();
        await harness.Deliveries.DeliverDueAsync();

        var smsRow = harness.Store.Notifications.Single(notification => notification.Reason.StartsWith("rule"));
        Assert.Equal(NotificationStatus.Dead, smsRow.Status);
        Assert.Equal(2, smsRow.Attempts);
        Assert.Single(email.Sent);
    }

    [Fact]
    public async Task A_channel_without_adapter_falls_back()
    {
        var harness = new Harness();
        var ui = new FakeChannel("ui", SendResult.Sent(null));
        harness.Channels.Adapters["ui"] = ui;
        await harness.SeedRuleAsync("rule-1", ["email"], "marek");
        await harness.AlertChanges.HandleAsync(Harness.Change(AlertChanges.Opened));

        await harness.Deliveries.DeliverDueAsync();
        await harness.Deliveries.DeliverDueAsync();

        Assert.Single(ui.Sent);
    }

    [Fact]
    public async Task An_ack_after_planning_cancels_instead_of_sending()
    {
        var harness = new Harness();
        var sms = new FakeChannel("sms", SendResult.Sent(null));
        harness.Channels.Adapters["sms"] = sms;
        await harness.SeedRuleAsync("rule-1", ["sms"], "marek");
        await harness.AlertChanges.HandleAsync(Harness.Change(AlertChanges.Opened));
        harness.Store.Alerts.Single().Apply(AlertStatus.Acked, "critical", null, "{}", harness.Clock.UtcNow);

        await harness.Deliveries.DeliverDueAsync();

        Assert.Empty(sms.Sent);
        Assert.Equal(NotificationStatus.Cancelled, harness.Store.Notifications.Single().Status);
        Assert.Contains(harness.Store.Outbox, message => message.Payload.Contains("suppressed by acked"));
    }

    [Fact]
    public async Task Sms_fits_160_characters_and_keeps_the_link()
    {
        var harness = new Harness();
        var sms = new FakeChannel("sms", SendResult.Sent(null));
        harness.Channels.Adapters["sms"] = sms;
        await harness.SeedRuleAsync("rule-1", ["sms"], "marek");
        await harness.AlertChanges.HandleAsync(Harness.Change(AlertChanges.Opened, title: new string('x', 300)));

        await harness.Deliveries.DeliverDueAsync();

        var text = Assert.Single(sms.Sent).Body;
        Assert.True(text.Length <= MessageRenderService.SmsLimit, $"SMS is {text.Length} characters.");
        Assert.EndsWith("/a/alert-1", text);
    }
}
