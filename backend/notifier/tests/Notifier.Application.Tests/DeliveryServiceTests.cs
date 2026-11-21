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

        await harness.DeliverDueAsync();

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

        await harness.DeliverDueAsync();
        harness.Clock.UtcNow = harness.Clock.UtcNow.AddMinutes(1);
        await harness.DeliverDueAsync();
        await harness.DeliverDueAsync();

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

        await harness.DeliverDueAsync();
        await harness.DeliverDueAsync();

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

        await harness.DeliverDueAsync();

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

        await harness.DeliverDueAsync();

        var text = Assert.Single(sms.Sent).Body;
        Assert.True(text.Length <= MessageRenderService.SmsLimit, $"SMS is {text.Length} characters.");
        Assert.EndsWith("/a/alert-1", text);
    }

    [Fact]
    public async Task A_row_due_now_wakes_the_delivery_loop_after_the_commit()
    {
        var harness = new Harness();
        await harness.SeedRuleAsync("rule-1", ["sms"], "marek");

        await harness.AlertChanges.HandleAsync(Harness.Change(AlertChanges.Opened));

        Assert.Equal(1, harness.Signal.Notified);
    }

    [Fact]
    public async Task A_plan_with_only_delayed_steps_does_not_wake_the_delivery_loop()
    {
        var harness = new Harness();
        await harness.SeedRuleAsync("rule-1", ["email"], "marek");

        await harness.AlertChanges.HandleAsync(Harness.Change(AlertChanges.Opened, severity: "warning"));

        Assert.True(Assert.Single(harness.Store.Notifications).SendAt > harness.Clock.UtcNow);
        Assert.Equal(0, harness.Signal.Notified);
    }

    [Fact]
    public async Task Configuration_is_read_once_until_a_config_change_arrives()
    {
        var harness = new Harness();
        await harness.SeedRuleAsync("rule-1", ["sms"], "marek");

        await harness.AlertChanges.HandleAsync(Harness.Change(AlertChanges.Opened, alertId: "alert-1", changeId: "change-1"));
        await harness.AlertChanges.HandleAsync(Harness.Change(AlertChanges.Opened, alertId: "alert-2", changeId: "change-2"));
        Assert.Equal(1, harness.Repositories.LoadAllCalls);

        await harness.Config.ApplyAsync(Harness.ConfigRecord("user", "ola", new { id = "ola", name = "Ola" }));
        await harness.AlertChanges.HandleAsync(Harness.Change(AlertChanges.Opened, alertId: "alert-3", changeId: "change-3"));
        Assert.Equal(2, harness.Repositories.LoadAllCalls);
    }

    [Fact]
    public async Task The_push_limit_holds_across_deliveries_and_folds_the_excess()
    {
        var harness = new Harness();
        var sms = new FakeChannel("sms", SendResult.Sent(null));
        harness.Channels.Adapters["sms"] = sms;
        await harness.SeedRuleAsync("rule-1", ["sms"], "marek");
        await harness.Config.ApplyAsync(Harness.ConfigRecord("preferences", "marek", new { user_id = "marek", max_push_per_hour = 1 }));
        foreach (var index in Enumerable.Range(1, 3))
        {
            await harness.AlertChanges.HandleAsync(Harness.Change(AlertChanges.Opened, alertId: $"alert-{index}", changeId: $"change-{index}", incidentId: $"incident-{index}"));
        }

        await harness.DeliverDueAsync();

        Assert.Single(sms.Sent);
        Assert.Equal(2, harness.Store.Notifications.Count(notification => notification.Status == NotificationStatus.Suppressed));
        Assert.Equal(2, harness.Store.RateCounters.Single().FoldedCount);
    }

    [Fact]
    public async Task The_push_counter_is_saved_before_the_provider_is_called()
    {
        var harness = new Harness();
        var savesAtSend = -1;
        var sms = new FakeChannel("sms", SendResult.Sent(null)) { OnSend = () => savesAtSend = harness.Repositories.SaveCalls };
        harness.Channels.Adapters["sms"] = sms;
        await harness.SeedRuleAsync("rule-1", ["sms"], "marek");
        await harness.Config.ApplyAsync(Harness.ConfigRecord("preferences", "marek", new { user_id = "marek", max_push_per_hour = 5 }));
        await harness.AlertChanges.HandleAsync(Harness.Change(AlertChanges.Opened));
        var savesBeforeDelivery = harness.Repositories.SaveCalls;

        await harness.DeliverDueAsync();

        Assert.Single(sms.Sent);
        Assert.Equal(savesBeforeDelivery + 1, savesAtSend);
    }
}
