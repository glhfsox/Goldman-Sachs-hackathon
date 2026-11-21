using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using AlertApi.Application.Constants;
using AlertApi.Application.Interfaces;
using AlertApi.Domain.Models;
using AlertApi.Infrastructure.Data;
using AlertApi.Tests.Infrastructure;

namespace AlertApi.Tests.Api;

[Trait("Category", "Integration")]
public sealed class RunRiskCheckTests : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-20T10:15:10Z");

    private const string Event = """
        {"type": "WORKFLOW_FAILED", "env": "prod", "mapped": true, "raw": {"workflowId": "WF-17"}}
        """;

    private const string Upload = """
        {"team_id": "settlements", "title": "Settlement timeout", "format": "markdown", "source": "1. Retry."}
        """;

    private const string AutoRunbook = """
        {"canonical": {"id": "settlement-timeout", "title": "T",
         "steps": [{"id": "retry", "text": "Retry", "action": "retry_workflow", "params": {"workflowId": "{{workflowId}}"}}]},
         "status": "active"}
        """;

    private const string Hold = """
        {"verdict": "hold", "confidence": 0.8, "reasons": ["it runs in production"]}
        """;

    private const string Clear = """
        {"verdict": "clear", "confidence": 0.9, "reasons": ["a retry is safe"]}
        """;

    private readonly PostgresFixture _postgres;

    public RunRiskCheckTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task NoVerdictByStartAt_HoldsTheRun_AndNothingStarts()
    {
        var (factory, client, clock) = await Prepare();
        using var _f = factory;
        using var _c = client;

        var tooEarly = await StartDue(factory);
        clock.Advance(TimeSpan.FromSeconds(31));
        var held = await StartDue(factory);
        var run = await LatestRun(client);

        Assert.False(tooEarly);
        Assert.True(held);
        Assert.Equal("waiting_approval", run.GetProperty("status").GetString());
        Assert.Equal("hold", run.GetProperty("risk_check").GetProperty("verdict").GetString());
        Assert.Equal("risk check did not answer", run.GetProperty("risk_check").GetProperty("reasons")[0].GetString());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("started_at").ValueKind);
        Assert.Contains("run_held", await TimelineKinds(client));
    }

    [Fact]
    public async Task Clear_StartsTheRunAtStartAt_NotEarlier()
    {
        var (factory, client, clock) = await Prepare();
        using var _f = factory;
        using var _c = client;
        var id = await LatestRunId(client);

        clock.Advance(TimeSpan.FromSeconds(1));
        var cleared = await Post(client, $"/api/runs/{id}/risk-check", Clear, "ai-worker");
        var early = await StartDue(factory);
        clock.Advance(TimeSpan.FromSeconds(30));
        var started = await StartDue(factory);
        var run = await LatestRun(client);

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.False(early);
        Assert.True(started);
        Assert.Equal("running", run.GetProperty("status").GetString());
        Assert.Equal("clear", run.GetProperty("risk_check").GetProperty("verdict").GetString());
        Assert.Equal(["run_scheduled", "run_cleared", "run_started"], (await TimelineKinds(client)).Where(kind => kind.StartsWith("run_")));
    }

    [Fact]
    public async Task Hold_MovesTheRunToAPerson_AndApproveStartsIt()
    {
        var (factory, client, _) = await Prepare();
        using var _f = factory;
        using var _c = client;
        var id = await LatestRunId(client);

        var held = await Post(client, $"/api/runs/{id}/risk-check", Hold, "ai-worker");
        var byService = await Post(client, $"/api/runs/{id}/approve", null, "ai-worker");
        var approved = await Post(client, $"/api/runs/{id}/approve", null, "anna");
        var run = await LatestRun(client);

        Assert.Equal("waiting_approval", (await held.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, byService.StatusCode);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal("running", run.GetProperty("status").GetString());
        Assert.Equal("anna", run.GetProperty("approved_by").GetString());
        Assert.Contains("run_held", await TimelineKinds(client));
    }

    [Fact]
    public async Task Hold_AfterAnEarlierClear_StillApplies()
    {
        var (factory, client, _) = await Prepare();
        using var _f = factory;
        using var _c = client;
        var id = await LatestRunId(client);

        await Post(client, $"/api/runs/{id}/risk-check", Clear, "ai-worker");
        await Post(client, $"/api/runs/{id}/risk-check", Hold, "ai-worker");

        Assert.Equal("waiting_approval", (await LatestRun(client)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task SameVerdictTwice_ChangesNothing_AndAClearAfterAHoldIsA409()
    {
        var (factory, client, _) = await Prepare();
        using var _f = factory;
        using var _c = client;
        var id = await LatestRunId(client);

        await Post(client, $"/api/runs/{id}/risk-check", Hold, "ai-worker");
        var again = await Post(client, $"/api/runs/{id}/risk-check", Hold, "ai-worker");
        var late = await Post(client, $"/api/runs/{id}/risk-check", Clear, "ai-worker");

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal(1, (await TimelineKinds(client)).Count(kind => kind == "run_held"));
    }

    [Fact]
    public async Task VerdictAfterStartAt_IsA409()
    {
        var (factory, client, clock) = await Prepare();
        using var _f = factory;
        using var _c = client;
        var id = await LatestRunId(client);
        await Post(client, $"/api/runs/{id}/risk-check", Clear, "ai-worker");
        clock.Advance(TimeSpan.FromSeconds(31));
        await StartDue(factory);

        var response = await Post(client, $"/api/runs/{id}/risk-check", Hold, "ai-worker");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("running", (await LatestRun(client)).GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("anna")]
    [InlineData(null)]
    public async Task RiskCheck_FromAnyoneButTheAiWorker_Is403(string? user)
    {
        var (factory, client, _) = await Prepare();
        using var _f = factory;
        using var _c = client;

        var response = await Post(client, $"/api/runs/{await LatestRunId(client)}/risk-check", Clear, user);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("""{"verdict": "maybe", "confidence": 0.5, "reasons": []}""")]
    [InlineData("""{"verdict": "hold", "confidence": 1.5, "reasons": []}""")]
    [InlineData("""{"verdict": "hold", "confidence": 0.5, "reasons": ["a","b","c","d","e","f","g"]}""")]
    [InlineData("""{"verdict": "hold", "reasons": []}""")]
    public async Task RiskCheck_WithABadBody_Is422(string body)
    {
        var (factory, client, _) = await Prepare();
        using var _f = factory;
        using var _c = client;

        var response = await Post(client, $"/api/runs/{await LatestRunId(client)}/risk-check", body, "ai-worker");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task AHeldRun_BlocksANewRun_AndResolveCancelsIt()
    {
        var (factory, client, _) = await Prepare();
        using var _f = factory;
        using var _c = client;
        var id = await LatestRunId(client);
        await Post(client, $"/api/runs/{id}/risk-check", Hold, "ai-worker");

        var second = await Post(client, "/api/alerts/alert-1/runs", """{"runbook_id": "settlement-timeout"}""", "anna");
        var resolve = await Post(client, "/api/alerts/alert-1/resolve", null, "anna");

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);
        Assert.Equal("cancelled", (await LatestRun(client)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task List_ShowsPendingRuns_UntilTheyHaveAVerdict()
    {
        var (factory, client, _) = await Prepare();
        using var _f = factory;
        using var _c = client;
        var id = await LatestRunId(client);

        var before = await client.GetFromJsonAsync<JsonElement>("/api/runs?risk_check=pending");
        await Post(client, $"/api/runs/{id}/risk-check", Clear, "ai-worker");
        var after = await client.GetFromJsonAsync<JsonElement>("/api/runs?risk_check=pending");

        Assert.Equal(id, before[0].GetProperty("id").GetString());
        Assert.Equal("retry_workflow", before[0].GetProperty("nodes")[0].GetProperty("action").GetString());
        Assert.Equal(0, after.GetArrayLength());
    }

    [Fact]
    public async Task List_ShowsFinishedRuns_OldestFirst_AndNeedsAFilter()
    {
        var (factory, client, _) = await Prepare();
        using var _f = factory;
        using var _c = client;
        var id = await LatestRunId(client);
        await Post(client, $"/api/runs/{id}/cancel", null, "anna");

        var finished = await client.GetFromJsonAsync<JsonElement>("/api/runs?finished_since=2026-11-20T10:00:00Z&limit=10");
        var later = await client.GetFromJsonAsync<JsonElement>("/api/runs?finished_since=2026-11-20T11:00:00Z");
        var none = await client.GetAsync("/api/runs");

        Assert.Equal("cancelled", finished[0].GetProperty("status").GetString());
        Assert.Equal("anna", finished[0].GetProperty("cancelled_by").GetString());
        Assert.Equal(0, later.GetArrayLength());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, none.StatusCode);
    }

    private async Task<(WebApplicationFactory<Program> Factory, HttpClient Client, TestClock Clock)> Prepare()
    {
        var clock = new TestClock(Start);
        var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false, clock: clock, riskCheck: true);
        var client = factory.CreateClient();

        await client.PostAsync("/api/runbooks", Json(Upload));
        await client.PutAsync("/api/runbooks/settlement-timeout", Json(AutoRunbook));
        Assert.Equal(HttpStatusCode.OK, (await Post(client, "/api/runbooks/settlement-timeout/accept?version=2", null, "anna")).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApiDbContext>();

            context.RunbookStarts.Add(RunbookStart.Create("settlements-workflow-failed", "settlement-timeout", "auto", "anna", Start));
            await context.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IActivityRecordHandler>()
                .HandleAsync(Topics.AlertsChanges, ActivityRecords.AlertChange("c-1", "opened", sampleEvent: Event));
        }

        return (factory, client, clock);
    }

    private static async Task<bool> StartDue(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IRunAutomation>().StartDueAsync();
    }

    private static async Task<string> LatestRunId(HttpClient client)
    {
        var alert = await client.GetFromJsonAsync<JsonElement>("/api/alerts/alert-1");

        return alert.GetProperty("runbook").GetProperty("run_id").GetString()!;
    }

    private static async Task<JsonElement> LatestRun(HttpClient client)
    {
        return await client.GetFromJsonAsync<JsonElement>($"/api/runs/{await LatestRunId(client)}");
    }

    private static async Task<List<string>> TimelineKinds(HttpClient client)
    {
        var alert = await client.GetFromJsonAsync<JsonElement>("/api/alerts/alert-1");

        return alert.GetProperty("timeline").EnumerateArray().Select(entry => entry.GetProperty("kind").GetString()!).ToList();
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string url, string? body, string? user)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);

        if (user is not null)
        {
            request.Headers.Add("X-User", user);
        }

        if (body is not null)
        {
            request.Content = Json(body);
        }

        return await client.SendAsync(request);
    }

    private static StringContent Json(string body)
    {
        return new StringContent(body, Encoding.UTF8, "application/json");
    }
}
