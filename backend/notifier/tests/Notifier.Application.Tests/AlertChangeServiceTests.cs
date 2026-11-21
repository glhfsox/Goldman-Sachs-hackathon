using Notifier.Application.Constants;
using Notifier.Domain.ValueObjects;

namespace Notifier.Application.Tests;

public sealed class AlertChangeServiceTests
{
    [Fact]
    public async Task Opened_plans_one_row_per_resolved_recipient_and_channel()
    {
        var harness = new Harness();
        await harness.SeedRuleAsync("rule-1", ["call", "sms", "ui"], "marek", "anna");

        await harness.AlertChanges.HandleAsync(Harness.Change(AlertChanges.Opened));

        Assert.Equal(6, harness.Store.Notifications.Count);
        Assert.Single(harness.Store.Alerts);
        Assert.Single(harness.Store.ProcessedChanges);
    }

    [Fact]
    public async Task Runbook_steps_are_filled_from_the_event()
    {
        var harness = new Harness();
        await harness.SeedRuleAsync("rule-1", ["email"], "marek");

        await harness.AlertChanges.HandleAsync(Harness.Change(AlertChanges.Opened));

        var content = harness.Store.Notifications.Single().Content;
        Assert.Equal("Settlement timeout", content.RunbookTitle);
        Assert.Equal("Retry workflow WF-42", Assert.Single(content.RunbookSteps));
    }

    [Fact]
    public async Task Without_a_rule_copy_nobody_is_notified()
    {
        var harness = new Harness();

        await harness.AlertChanges.HandleAsync(Harness.Change(AlertChanges.Opened, ruleId: "unknown"));

        Assert.Empty(harness.Store.Notifications);
        Assert.Single(harness.Store.ProcessedChanges);
    }

    [Fact]
    public async Task A_redelivered_change_is_ignored()
    {
        var harness = new Harness();
        await harness.SeedRuleAsync("rule-1", ["sms"], "marek");
        var change = Harness.Change(AlertChanges.Opened);

        await harness.AlertChanges.HandleAsync(change);
        await harness.AlertChanges.HandleAsync(change);

        Assert.Single(harness.Store.Notifications);
    }

    [Fact]
    public async Task A_person_matched_by_two_rules_of_one_incident_gets_one_message()
    {
        var harness = new Harness();
        await harness.SeedRuleAsync("rule-1", ["sms"], "marek");
        await harness.SeedRuleAsync("rule-2", ["sms"], "marek");

        await harness.AlertChanges.HandleAsync(Harness.Change(
            AlertChanges.Opened, alertId: "alert-1", ruleId: "rule-1", incidentId: "incident-1", changeId: "change-1"));
        await harness.AlertChanges.HandleAsync(Harness.Change(
            AlertChanges.Opened, alertId: "alert-2", ruleId: "rule-2", incidentId: "incident-1", changeId: "change-2"));

        Assert.Single(harness.Store.Notifications, notification => notification.IsOpen);
        Assert.Equal(NotificationStatus.Suppressed, harness.Store.Notifications.Single(notification => notification.AlertId == "alert-2").Status);
    }

    [Fact]
    public async Task Ack_cancels_every_pending_ladder_step_and_tells_the_timeline()
    {
        var harness = new Harness();
        await harness.SeedRuleAsync("rule-1", ["email", "sms", "ui"], "marek");
        await harness.AlertChanges.HandleAsync(Harness.Change(AlertChanges.Opened, severity: "warning"));

        await harness.AlertChanges.HandleAsync(Harness.Change(AlertChanges.Acked, severity: "warning", changeId: "change-ack"));

        Assert.All(harness.Store.Notifications, notification => Assert.Equal(NotificationStatus.Cancelled, notification.Status));
        Assert.Equal(3, harness.Store.Outbox.Count(message =>
            message.Topic == Topics.AlertsTimeline && message.Payload.Contains("suppressed by acked")));
    }

    [Fact]
    public async Task Count_threshold_never_notifies()
    {
        var harness = new Harness();
        await harness.SeedRuleAsync("rule-1", ["sms"], "marek");

        await harness.AlertChanges.HandleAsync(Harness.Change(AlertChanges.CountThreshold));

        Assert.Empty(harness.Store.Notifications);
    }

    [Fact]
    public async Task Severity_raised_replaces_the_delayed_plan()
    {
        var harness = new Harness();
        await harness.SeedRuleAsync("rule-1", ["sms"], "marek");
        await harness.AlertChanges.HandleAsync(Harness.Change(AlertChanges.Opened, severity: "warning"));

        await harness.AlertChanges.HandleAsync(Harness.Change(AlertChanges.SeverityRaised, severity: "critical", changeId: "change-2"));

        var open = Assert.Single(harness.Store.Notifications, notification => notification.IsOpen);
        Assert.Equal(harness.Clock.UtcNow, open.SendAt);
    }

    [Fact]
    public async Task A_batch_is_saved_once()
    {
        var harness = new Harness();
        await harness.SeedRuleAsync("rule-1", ["sms"], "marek");
        var saves = harness.Repositories.SaveCalls;

        await harness.AlertChanges.HandleBatchAsync([
            Harness.Change(AlertChanges.Opened, alertId: "alert-1", changeId: "change-1"),
            Harness.Change(AlertChanges.Opened, alertId: "alert-2", changeId: "change-2")]);

        Assert.Equal(saves + 1, harness.Repositories.SaveCalls);
        Assert.Equal(2, harness.Store.Notifications.Count);
        Assert.Equal(2, harness.Store.ProcessedChanges.Count);
    }

    [Fact]
    public async Task An_ack_in_the_same_batch_cancels_what_the_open_planned()
    {
        var harness = new Harness();
        await harness.SeedRuleAsync("rule-1", ["email", "sms", "ui"], "marek");

        await harness.AlertChanges.HandleBatchAsync([
            Harness.Change(AlertChanges.Opened, severity: "warning"),
            Harness.Change(AlertChanges.Acked, severity: "warning", changeId: "change-ack")]);

        Assert.NotEmpty(harness.Store.Notifications);
        Assert.All(harness.Store.Notifications, notification => Assert.Equal(NotificationStatus.Cancelled, notification.Status));
    }

    [Fact]
    public async Task Two_alerts_of_one_incident_in_one_batch_send_one_message_per_person()
    {
        var harness = new Harness();
        await harness.SeedRuleAsync("rule-1", ["sms"], "marek");
        await harness.SeedRuleAsync("rule-2", ["sms"], "marek");

        await harness.AlertChanges.HandleBatchAsync([
            Harness.Change(AlertChanges.Opened, alertId: "alert-1", ruleId: "rule-1", incidentId: "incident-1", changeId: "change-1"),
            Harness.Change(AlertChanges.Opened, alertId: "alert-2", ruleId: "rule-2", incidentId: "incident-1", changeId: "change-2")]);

        Assert.Single(harness.Store.Notifications, notification => notification.IsOpen);
        Assert.Equal(NotificationStatus.Suppressed, harness.Store.Notifications.Single(notification => notification.AlertId == "alert-2").Status);
    }

    [Fact]
    public async Task A_change_repeated_within_a_batch_and_an_invalid_record_are_skipped()
    {
        var harness = new Harness();
        await harness.SeedRuleAsync("rule-1", ["sms"], "marek");
        var change = Harness.Change(AlertChanges.Opened);
        var invalid = change with { Alert = default };

        await harness.AlertChanges.HandleBatchAsync([change, invalid, change]);

        Assert.Single(harness.Store.Notifications);
        Assert.Single(harness.Store.ProcessedChanges);
    }

    [Fact]
    public async Task A_deleted_membership_disappears_from_the_copy()
    {
        var harness = new Harness();
        await harness.Config.ApplyAsync(Harness.ConfigRecord("membership", "settlements:marek", new { team_id = "settlements", user_id = "marek", on_call = true }));

        await harness.Config.ApplyAsync(Harness.ConfigRecord("membership", "settlements:marek", body: null, deleted: true));

        Assert.Empty(harness.Store.Config);
    }
}
