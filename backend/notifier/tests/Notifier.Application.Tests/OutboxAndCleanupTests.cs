using Microsoft.Extensions.Logging.Abstractions;

using Notifier.Application.Options;
using Notifier.Application.Services;
using Notifier.Application.Tests.Fakes;
using Notifier.Domain.Models;
using Notifier.Domain.ValueObjects;

using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Notifier.Application.Tests;

public sealed class OutboxAndCleanupTests
{
    [Fact]
    public async Task A_failed_record_stays_pending_while_the_rest_of_the_batch_is_published()
    {
        var harness = new Harness();
        var publisher = new FakePublisher { FailWhenPayloadContains = "broken" };
        var outbox = new OutboxService(
            harness.Repositories,
            publisher,
            harness.Clock,
            OptionsFactory.Create(new OutboxOptions()),
            NullLogger<OutboxService>.Instance);
        harness.Store.Outbox.Add(OutboxMessage.Create("alerts.timeline", "alert-1", "{\"n\":1}", harness.Clock.UtcNow));
        harness.Store.Outbox.Add(OutboxMessage.Create("alerts.timeline", "alert-2", "{\"broken\":true}", harness.Clock.UtcNow));
        harness.Store.Outbox.Add(OutboxMessage.Create("alerts.timeline", "alert-3", "{\"n\":3}", harness.Clock.UtcNow));

        var handled = await outbox.DispatchAsync();

        Assert.Equal(3, handled);
        Assert.Equal(2, publisher.Published.Count);
        var failed = harness.Store.Outbox.Single(message => message.Key == "alert-2");
        Assert.Null(failed.ProcessedAt);
        Assert.Equal("broker unavailable", failed.Error);
        Assert.All(harness.Store.Outbox.Where(message => message.Key != "alert-2"), message => Assert.NotNull(message.ProcessedAt));
    }

    [Fact]
    public async Task Cleanup_deletes_only_old_rows_in_a_final_state()
    {
        var harness = new Harness();
        var sms = new FakeChannel("sms", Notifier.Application.DTOs.Channels.SendResult.Sent(null));
        harness.Channels.Adapters["sms"] = sms;
        await harness.SeedRuleAsync("rule-1", ["sms"], "marek");
        await harness.AlertChanges.HandleAsync(Harness.Change("opened", alertId: "alert-1", changeId: "change-1"));
        await harness.DeliverDueAsync();
        harness.Store.Outbox.ForEach(message => message.MarkProcessed(harness.Clock.UtcNow));

        // Eight days later a new alert is still pending; the old sent row, its change and its records are gone.
        harness.Clock.UtcNow = harness.Clock.UtcNow.AddDays(8);
        await harness.AlertChanges.HandleAsync(Harness.Change("opened", alertId: "alert-2", changeId: "change-2", incidentId: "incident-2"));
        var cleanup = new CleanupService(
            harness.Repositories,
            harness.Repositories,
            harness.Repositories,
            harness.Clock,
            OptionsFactory.Create(new CleanupOptions()),
            NullLogger<CleanupService>.Instance);

        var deleted = await cleanup.CleanUpAsync();

        Assert.True(deleted >= 3, $"deleted {deleted}");
        var left = Assert.Single(harness.Store.Notifications);
        Assert.Equal("alert-2", left.AlertId);
        Assert.Equal(NotificationStatus.Pending, left.Status);
        Assert.Equal("change-2", Assert.Single(harness.Store.ProcessedChanges).ChangeId);
    }
}
