using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using AlertApi.Application.Constants;
using AlertApi.Application.Interfaces;
using AlertApi.Domain.Models;
using AlertApi.Infrastructure.Data;
using AlertApi.Tests.Infrastructure;

namespace AlertApi.Tests.Api;

[Trait("Category", "Integration")]
public sealed class AutoRunTests : IClassFixture<PostgresFixture>
{
    // The alert changes of ActivityRecords are stamped 10:15:02; "now" is a few seconds later.
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-20T10:15:10Z");

    private const string Event = """
        {"type": "WORKFLOW_FAILED", "env": "prod", "mapped": true, "raw": {"workflowId": "WF-17"}}
        """;

    private const string Upload = """
        {"team_id": "settlements", "title": "Settlement timeout", "format": "markdown", "source": "1. Retry."}
        """;

    private const string ReadOnlyRunbook = """
        {"canonical": {"id": "settlement-timeout", "title": "T",
         "steps": [{"id": "health", "text": "Check", "action": "http_get", "params": {"url": "http://counterparty-service/health"}}]},
         "status": "active"}
        """;

    private const string AutoRunbook = """
        {"canonical": {"id": "settlement-timeout", "title": "T",
         "steps": [{"id": "retry", "text": "Retry", "action": "retry_workflow", "params": {"workflowId": "{{workflowId}}"}}]},
         "status": "active"}
        """;

    private const string ApprovalRunbook = """
        {"canonical": {"id": "settlement-timeout", "title": "T",
         "steps": [{"id": "escalate", "text": "Escalate", "action": "escalate", "params": {"team": "counterparty-support"}}]},
         "status": "active"}
        """;

    private readonly PostgresFixture _postgres;

    public AutoRunTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task Opened_WithAReadOnlyRunbook_StartsARunAtOnce_WithNoApprover()
    {
        var (factory, client, _) = await Prepare(ReadOnlyRunbook);
        using var _f = factory;
        using var _c = client;

        await Apply(factory, ActivityRecords.AlertChange("c-1", "opened", sampleEvent: Event));
        var run = await LatestRun(client);

        Assert.Equal("running", run.GetProperty("status").GetString());
        Assert.Equal("auto", run.GetProperty("trigger").GetString());
        Assert.Equal("read_only", run.GetProperty("risk").GetString());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("approved_by").ValueKind);
    }

    [Fact]
    public async Task Opened_WithAnAutoRunbook_SchedulesTheRun_ThenTheSchedulerStartsItAfterTheWindow()
    {
        var (factory, client, clock) = await Prepare(AutoRunbook);
        using var _f = factory;
        using var _c = client;
        await SetStart(factory, "auto");

        await Apply(factory, ActivityRecords.AlertChange("c-1", "opened", sampleEvent: Event));
        var scheduled = await LatestRun(client);
        var tooEarly = await StartDue(factory);
        clock.Advance(TimeSpan.FromSeconds(31));
        var started = await StartDue(factory);
        var running = await LatestRun(client);
        var timeline = (await client.GetFromJsonAsync<JsonElement>("/api/alerts/alert-1")).GetProperty("timeline");

        Assert.Equal("scheduled", scheduled.GetProperty("status").GetString());
        Assert.Equal(Start.AddSeconds(30), scheduled.GetProperty("start_at").GetDateTimeOffset());
        Assert.False(tooEarly);
        Assert.True(started);
        Assert.Equal("running", running.GetProperty("status").GetString());
        Assert.Equal(
            ["run_scheduled", "run_started"],
            timeline.EnumerateArray().Select(entry => entry.GetProperty("kind").GetString()!).Where(kind => kind.StartsWith("run_")));
    }

    [Fact]
    public async Task Opened_WithAnAutoRunbook_StartsNothing_WithoutAnAutoStartRow()
    {
        var (factory, client, _) = await Prepare(AutoRunbook);
        using var _f = factory;
        using var _c = client;

        await Apply(factory, ActivityRecords.AlertChange("c-1", "opened", sampleEvent: Event));

        Assert.Equal(JsonValueKind.Null, (await Alert(client)).GetProperty("runbook").ValueKind);
    }

    [Fact]
    public async Task Opened_WithAnApprovalRunbook_StartsNothing_EvenWithAnAutoStartRow()
    {
        var (factory, client, _) = await Prepare(ApprovalRunbook);
        using var _f = factory;
        using var _c = client;
        await SetStart(factory, "auto");

        await Apply(factory, ActivityRecords.AlertChange("c-1", "opened", sampleEvent: Event));

        Assert.Equal(JsonValueKind.Null, (await Alert(client)).GetProperty("runbook").ValueKind);
    }

    [Fact]
    public async Task OpenedTwice_StartsOneRun()
    {
        var (factory, client, _) = await Prepare(ReadOnlyRunbook);
        using var _f = factory;
        using var _c = client;

        await Apply(factory, ActivityRecords.AlertChange("c-1", "opened", sampleEvent: Event));
        await Apply(factory, ActivityRecords.AlertChange("c-1", "opened", sampleEvent: Event));

        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ApiDbContext>().Runs.CountAsync());
    }

    [Fact]
    public async Task Opened_LongAfterTheChange_StartsNothing()
    {
        var (factory, client, clock) = await Prepare(ReadOnlyRunbook);
        using var _f = factory;
        using var _c = client;
        clock.Advance(TimeSpan.FromMinutes(10));

        await Apply(factory, ActivityRecords.AlertChange("c-1", "opened", sampleEvent: Event));

        Assert.Equal(JsonValueKind.Null, (await Alert(client)).GetProperty("runbook").ValueKind);
    }

    [Fact]
    public async Task Resolved_CancelsAScheduledRun_SoItNeverStarts()
    {
        var (factory, client, clock) = await Prepare(AutoRunbook);
        using var _f = factory;
        using var _c = client;
        await SetStart(factory, "auto");
        await Apply(factory, ActivityRecords.AlertChange("c-1", "opened", sampleEvent: Event));

        await Apply(factory, ActivityRecords.AlertChange("c-2", "resolved", status: "resolved", sampleEvent: Event));
        clock.Advance(TimeSpan.FromSeconds(31));
        var started = await StartDue(factory);
        var run = await LatestRun(client);

        Assert.False(started);
        Assert.Equal("cancelled", run.GetProperty("status").GetString());
        Assert.Equal("processor", run.GetProperty("cancelled_by").GetString());
    }

    [Fact]
    public async Task ResolveByAPerson_CancelsTheLiveRun_InTheSameSave()
    {
        var (factory, client, _) = await Prepare(AutoRunbook);
        using var _f = factory;
        using var _c = client;
        await SetStart(factory, "auto");
        await Apply(factory, ActivityRecords.AlertChange("c-1", "opened", sampleEvent: Event));

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/alerts/alert-1/resolve");
        request.Headers.Add("X-User", "anna");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        var run = await LatestRun(client);

        Assert.Equal("cancelled", run.GetProperty("status").GetString());
        Assert.Equal("anna", run.GetProperty("cancelled_by").GetString());
    }

    private async Task<(WebApplicationFactory<Program> Factory, HttpClient Client, TestClock Clock)> Prepare(string canonical)
    {
        var clock = new TestClock(Start);
        var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false, clock: clock);
        var client = factory.CreateClient();

        await client.PostAsync("/api/runbooks", Json(Upload));
        await client.PutAsync("/api/runbooks/settlement-timeout", Json(canonical));

        var accept = new HttpRequestMessage(HttpMethod.Post, "/api/runbooks/settlement-timeout/accept?version=2");
        accept.Headers.Add("X-User", "anna");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(accept)).StatusCode);

        return (factory, client, clock);
    }

    private static async Task SetStart(WebApplicationFactory<Program> factory, string mode)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApiDbContext>();

        context.RunbookStarts.Add(RunbookStart.Create("settlements-workflow-failed", "settlement-timeout", mode, "anna", Start));
        await context.SaveChangesAsync();
    }

    private static async Task<bool> StartDue(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IRunAutomation>().StartDueAsync();
    }

    private static async Task<JsonElement> Alert(HttpClient client)
    {
        return await client.GetFromJsonAsync<JsonElement>("/api/alerts/alert-1");
    }

    private static async Task<JsonElement> LatestRun(HttpClient client)
    {
        var id = (await Alert(client)).GetProperty("runbook").GetProperty("run_id").GetString();

        return await client.GetFromJsonAsync<JsonElement>($"/api/runs/{id}");
    }

    private static async Task Apply(WebApplicationFactory<Program> factory, string value)
    {
        using var scope = factory.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<IActivityRecordHandler>().HandleAsync(Topics.AlertsChanges, value);
    }

    private static StringContent Json(string body)
    {
        return new StringContent(body, Encoding.UTF8, "application/json");
    }
}
