using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Notifier.Application.DTOs.Messages;
using Notifier.Application.Options;
using Notifier.Application.Serialization;
using Notifier.Application.Services;
using Notifier.Application.Tests.Fakes;
using Notifier.Domain.Services;

using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Notifier.Application.Tests;

public sealed class Harness
{
    public Harness(DeliveryOptions? delivery = null)
    {
        Repositories = new FakeRepositories(Store);
        var repositories = Repositories;
        var timeline = new TimelineService(repositories);
        DeliveryOptions = delivery ?? new DeliveryOptions();
        ConfigCache = new ConfigSnapshotCache(Clock, OptionsFactory.Create(new ConfigCacheOptions()));

        Config = new ConfigChangeService(repositories, ConfigCache, NullLogger<ConfigChangeService>.Instance);
        AlertChanges = new AlertChangeService(
            repositories,
            repositories,
            repositories,
            repositories,
            timeline,
            ConfigCache,
            Signal,
            new EscalationLadder(),
            new RecipientResolver(),
            Clock,
            OptionsFactory.Create(new EscalationOptions()),
            NullLogger<AlertChangeService>.Instance);
        Deliveries = new DeliveryService(
            repositories,
            repositories,
            repositories,
            timeline,
            new MessageRenderService(OptionsFactory.Create(DeliveryOptions)),
            Channels,
            new DeliveryGate(),
            new FallbackPolicy(),
            Clock,
            OptionsFactory.Create(DeliveryOptions),
            NullLogger<DeliveryService>.Instance);
    }

    public InMemoryStore Store { get; } = new();

    public FakeRepositories Repositories { get; }

    public FakeDeliverySignal Signal { get; } = new();

    public ConfigSnapshotCache ConfigCache { get; }

    public FakeClock Clock { get; } = new();

    public FakeChannelRegistry Channels { get; } = new();

    public DeliveryOptions DeliveryOptions { get; }

    public ConfigChangeService Config { get; }

    public AlertChangeService AlertChanges { get; }

    public DeliveryService Deliveries { get; }

    // What DeliveryWorker does, one notification after another: claim the due rows, then deliver each.
    public async Task DeliverDueAsync()
    {
        foreach (var id in await Deliveries.ClaimDueAsync(50))
        {
            await Deliveries.DeliverAsync(id);
        }
    }

    // What the api would publish on config.changes: a rule routing to the named users, and those users.
    public async Task SeedRuleAsync(string ruleId, string[]? channels, params string[] userIds)
    {
        foreach (var userId in userIds)
        {
            await Config.ApplyAsync(ConfigRecord("user", userId, new { id = userId, name = userId, email = $"{userId}@example.com", phone = "+48000000000" }));
        }

        var rule = new
        {
            id = ruleId,
            team_id = "settlements",
            enabled = true,
            window_s = 300,
            route = new { recipients = userIds.Select(userId => new { user = userId }).ToArray(), channels },
            runbook_id = "settlement-timeout"
        };
        await Config.ApplyAsync(ConfigRecord("rule", ruleId, rule));
        await Config.ApplyAsync(ConfigRecord("runbook", "settlement-timeout", new
        {
            id = "settlement-timeout",
            team_id = "settlements",
            title = "Settlement timeout",
            status = "active",
            version = 1,
            canonical = new { title = "Settlement timeout", steps = new[] { new { text = "Retry workflow {{workflowId}}" } } }
        }));
    }

    public static ConfigChangeMessage ConfigRecord(string kind, string id, object? body, bool deleted = false)
    {
        var json = JsonSerializer.Serialize(new { kind, id, updated_at = "2026-11-20T09:00:00.000Z", deleted, body });

        return JsonSerializer.Deserialize<ConfigChangeMessage>(json, NotifierJson.Options)!;
    }

    public static AlertChangeMessage Change(
        string change,
        string alertId = "alert-1",
        string ruleId = "rule-1",
        string severity = "critical",
        string? incidentId = null,
        string changeId = "change-1",
        string title = "order-settlement failed at step settle")
    {
        var record = new Dictionary<string, object?>
        {
            ["change_id"] = changeId,
            ["change"] = change,
            ["at"] = "2026-11-20T10:00:00.000Z",
            ["actor"] = "processor",
            ["alert"] = new Dictionary<string, object?>
            {
                ["id"] = alertId,
                ["rule_id"] = ruleId,
                ["team_id"] = "settlements",
                ["incident_id"] = incidentId,
                ["status"] = change == "acked" ? "acked" : "open",
                ["severity"] = severity,
                ["title"] = title,
                ["body"] = "Counterparty service did not respond in 5000 ms",
                ["fields"] = new Dictionary<string, object> { ["step"] = "settle" },
                ["count"] = 1,
                ["sample_event"] = new Dictionary<string, object>
                {
                    ["event_id"] = "event-1",
                    ["raw"] = new Dictionary<string, object> { ["workflowId"] = "WF-42" }
                }
            }
        };
        var json = JsonSerializer.Serialize(record);

        return JsonSerializer.Deserialize<AlertChangeMessage>(json, NotifierJson.Options)!;
    }
}
