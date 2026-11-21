using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using AlertApi.Application.Constants;
using AlertApi.Application.Interfaces;
using AlertApi.Tests.Infrastructure;

namespace AlertApi.Tests.Api;

[Trait("Category", "Integration")]
public sealed class RunEndpointTests : IClassFixture<PostgresFixture>
{
    private const string Event = """
        {"type": "WORKFLOW_FAILED", "env": "prod", "mapped": true, "raw": {"workflowId": "WF-17"}}
        """;

    private const string Upload = """
        {"team_id": "settlements", "title": "Settlement timeout", "format": "markdown", "source": "1. Retry."}
        """;

    // A health check, a retry, an escalation only if the retry failed, and a note for a person.
    private const string Canonical = """
        {"canonical": {"id": "settlement-timeout", "title": "Settlement timeout",
         "steps": [
          {"id": "health", "text": "Is counterparty-service healthy?", "action": "http_get",
           "params": {"url": "http://counterparty-service/health"}},
          {"id": "retry", "text": "Retry {{workflowId}}", "action": "retry_workflow", "needs": ["health"],
           "params": {"workflowId": "{{workflowId}}"}, "retries": 1},
          {"id": "escalate", "text": "Escalate", "action": "escalate", "needs": ["retry"],
           "if": {"step": "retry", "status": "failed"}, "params": {"team": "counterparty-support"}},
          {"id": "note", "text": "Post an update in #payments-incidents"}]},
         "status": "active"}
        """;

    private readonly PostgresFixture _postgres;

    public RunEndpointTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task Preview_ShowsFilledParamsAndTheRunRisk_AndStoresNothing()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await Prepare(factory, client);

        var preview = await client.GetFromJsonAsync<JsonElement>("/api/alerts/alert-1/runs/preview?runbook_id=settlement-timeout");
        var nodes = preview.GetProperty("nodes").EnumerateArray().ToList();

        Assert.Equal("approval", preview.GetProperty("risk").GetString());
        Assert.Equal(["health", "retry", "escalate", "note"], nodes.Select(node => node.GetProperty("id").GetString()));
        Assert.Equal("WF-17", nodes[1].GetProperty("params").GetProperty("workflowId").GetString());
        Assert.Equal("Retry WF-17", nodes[1].GetProperty("text").GetString());
        Assert.Equal("approval", nodes[2].GetProperty("risk").GetString());
        Assert.Empty(nodes[1].GetProperty("missing").EnumerateArray());
    }

    [Fact]
    public async Task Start_ByAPerson_CreatesARunningRun_WithTheFirstStepReady_AndTheRestWaiting()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await Prepare(factory, client);

        var response = await Start(client, "anna");
        var run = await response.Content.ReadFromJsonAsync<JsonElement>();
        var nodes = run.GetProperty("nodes").EnumerateArray().ToDictionary(node => node.GetProperty("id").GetString()!);
        var detail = await client.GetFromJsonAsync<JsonElement>("/api/alerts/alert-1");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.StartsWith("run-", run.GetProperty("id").GetString());
        Assert.Equal("running", run.GetProperty("status").GetString());
        Assert.Equal("approval", run.GetProperty("risk").GetString());
        Assert.Equal("manual", run.GetProperty("trigger").GetString());
        Assert.Equal("anna", run.GetProperty("approved_by").GetString());
        Assert.Equal("ready", nodes["health"].GetProperty("status").GetString());
        Assert.Equal("pending", nodes["retry"].GetProperty("status").GetString());
        Assert.Equal("pending", nodes["escalate"].GetProperty("status").GetString());
        Assert.Equal("pending", nodes["note"].GetProperty("status").GetString());
        Assert.Contains(
            detail.GetProperty("timeline").EnumerateArray(),
            entry => entry.GetProperty("kind").GetString() == "run_started" && entry.GetProperty("actor").GetString() == "anna");
    }

    [Fact]
    public async Task Start_WithAMissingPlaceholder_BlocksThatStep_AndTheRunFails()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await Prepare(factory, client, sampleEvent: """{"type": "WORKFLOW_FAILED", "mapped": true, "raw": {}}""");

        var preview = await client.GetFromJsonAsync<JsonElement>("/api/alerts/alert-1/runs/preview?runbook_id=settlement-timeout");
        var run = await (await Start(client, "anna")).Content.ReadFromJsonAsync<JsonElement>();
        var nodes = run.GetProperty("nodes").EnumerateArray().ToDictionary(node => node.GetProperty("id").GetString()!);

        Assert.Equal(["workflowId"], preview.GetProperty("nodes")[1].GetProperty("missing").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("ready", nodes["health"].GetProperty("status").GetString());
        Assert.Equal("blocked", nodes["retry"].GetProperty("status").GetString());
        Assert.Equal("skipped", nodes["escalate"].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Start_IsRefused_ForAServiceActor_ASecondLiveRun_AnUnacceptedRunbook_AndAnUnknownRunbook()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await Prepare(factory, client);

        var service = await Start(client, "ai-worker");
        var first = await Start(client, "anna");
        var second = await Start(client, "boris");
        var unknown = await Start(client, "anna", runbookId: "nope");
        var missing = await Start(client, "anna", runbookId: null);
        await client.PutAsync("/api/runbooks/settlement-timeout", Json(Canonical));
        await Apply(factory, ActivityRecords.AlertChange("c-2", "opened", alertId: "alert-2", sampleEvent: Event));
        var unaccepted = await Start(client, "anna", alertId: "alert-2");

        Assert.Equal(HttpStatusCode.Forbidden, service.StatusCode);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, unaccepted.StatusCode);
    }

    [Fact]
    public async Task Start_OnAResolvedAlert_IsRefused()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await Prepare(factory, client);
        await Apply(factory, ActivityRecords.AlertChange("c-9", "resolved", status: "resolved", sampleEvent: Event));

        Assert.Equal(HttpStatusCode.Conflict, (await Start(client, "anna")).StatusCode);
    }

    [Fact]
    public async Task Cancel_StopsEveryStepNotStarted_AndLetsAnotherRunStart()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await Prepare(factory, client);
        var run = await (await Start(client, "anna")).Content.ReadFromJsonAsync<JsonElement>();
        var id = run.GetProperty("id").GetString();

        var service = await Post(client, $"/api/runs/{id}/cancel", "ai-worker");
        var cancelled = await Post(client, $"/api/runs/{id}/cancel", "boris");
        var again = await Post(client, $"/api/runs/{id}/cancel", "boris");
        var body = await cancelled.Content.ReadFromJsonAsync<JsonElement>();
        var fetched = await client.GetFromJsonAsync<JsonElement>($"/api/runs/{id}");

        Assert.Equal(HttpStatusCode.Forbidden, service.StatusCode);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal("cancelled", body.GetProperty("status").GetString());
        Assert.Equal("boris", body.GetProperty("cancelled_by").GetString());
        Assert.All(fetched.GetProperty("nodes").EnumerateArray(), node => Assert.Equal("cancelled", node.GetProperty("status").GetString()));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Start(client, "anna")).StatusCode);
    }

    [Fact]
    public async Task Tick_MarksATextStepDone_AndRefusesAnythingElse()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await Prepare(factory, client);
        var run = await (await Start(client, "anna")).Content.ReadFromJsonAsync<JsonElement>();
        var id = run.GetProperty("id").GetString();

        var action = await Post(client, $"/api/runs/{id}/nodes/health/done", "anna");
        var unknown = await Post(client, $"/api/runs/{id}/nodes/zzz/done", "anna");
        var service = await Post(client, $"/api/runs/{id}/nodes/note/done", "ai-worker");
        var ticked = await Post(client, $"/api/runs/{id}/nodes/note/done", "anna");
        var twice = await Post(client, $"/api/runs/{id}/nodes/note/done", "anna");
        var body = await ticked.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Conflict, action.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, service.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ticked.StatusCode);
        Assert.Equal("succeeded", body.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
    }

    [Fact]
    public async Task ARunWithOnlyTextSteps_IsDoneWhenThePersonTicksThem()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await Prepare(factory, client, canonical: """
            {"canonical": {"id": "settlement-timeout", "title": "T", "steps": [{"text": "Look at the dashboard"}]}, "status": "active"}
            """);
        var run = await (await Start(client, "anna")).Content.ReadFromJsonAsync<JsonElement>();
        var id = run.GetProperty("id").GetString();

        await Post(client, $"/api/runs/{id}/nodes/step-0/done", "anna");
        var fetched = await client.GetFromJsonAsync<JsonElement>($"/api/runs/{id}");
        var detail = await client.GetFromJsonAsync<JsonElement>("/api/alerts/alert-1");

        Assert.Equal("running", run.GetProperty("status").GetString());
        Assert.Equal("done", fetched.GetProperty("status").GetString());
        Assert.Contains(detail.GetProperty("timeline").EnumerateArray(), entry => entry.GetProperty("kind").GetString() == "run_done");
    }

    [Fact]
    public async Task AlertDetail_ShowsTheLatestRunsProgress_AndNullBeforeAnyRun()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await Prepare(factory, client, canonical: """
            {"canonical": {"id": "settlement-timeout", "title": "T",
             "steps": [{"id": "look", "text": "Look at the dashboard"}, {"id": "post", "text": "Post an update", "needs": ["look"]}]},
             "status": "active"}
            """);

        var before = await client.GetFromJsonAsync<JsonElement>("/api/alerts/alert-1");
        var run = await (await Start(client, "anna")).Content.ReadFromJsonAsync<JsonElement>();
        var id = run.GetProperty("id").GetString();
        var started = (await client.GetFromJsonAsync<JsonElement>("/api/alerts/alert-1")).GetProperty("runbook");
        await Post(client, $"/api/runs/{id}/nodes/look/done", "anna");
        var ticked = (await client.GetFromJsonAsync<JsonElement>("/api/alerts/alert-1")).GetProperty("runbook");
        await Post(client, $"/api/runs/{id}/nodes/post/done", "anna");
        var finished = (await client.GetFromJsonAsync<JsonElement>("/api/alerts/alert-1")).GetProperty("runbook");

        Assert.Equal(JsonValueKind.Null, before.GetProperty("runbook").ValueKind);
        Assert.Equal(id, started.GetProperty("run_id").GetString());
        Assert.Equal("settlement-timeout", started.GetProperty("runbook_id").GetString());
        Assert.Equal("running", started.GetProperty("status").GetString());
        Assert.Equal((2, 0), (started.GetProperty("steps_total").GetInt32(), started.GetProperty("steps_done").GetInt32()));
        Assert.Equal(1, ticked.GetProperty("steps_done").GetInt32());
        Assert.Equal(["succeeded", "pending"], ticked.GetProperty("steps").EnumerateArray().Select(step => step.GetProperty("status").GetString()));
        Assert.Equal("done", finished.GetProperty("status").GetString());
        Assert.Equal(2, finished.GetProperty("steps_done").GetInt32());
    }

    [Fact]
    public async Task AlertDetail_ForACancelledRun_ShowsCancelledSteps_AndItStaysTheLatest()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await Prepare(factory, client);
        var run = await (await Start(client, "anna")).Content.ReadFromJsonAsync<JsonElement>();

        await Post(client, $"/api/runs/{run.GetProperty("id").GetString()}/cancel", "anna");
        var progress = (await client.GetFromJsonAsync<JsonElement>("/api/alerts/alert-1")).GetProperty("runbook");

        Assert.Equal("cancelled", progress.GetProperty("status").GetString());
        Assert.Equal(4, progress.GetProperty("steps_total").GetInt32());
        Assert.Equal(0, progress.GetProperty("steps_done").GetInt32());
        Assert.All(progress.GetProperty("steps").EnumerateArray(), step => Assert.Equal("cancelled", step.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task GetRun_ForAnUnknownId_Is404()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/runs/run-none")).StatusCode);
    }

    private static async Task Prepare(
        WebApplicationFactory<Program> factory,
        HttpClient client,
        string sampleEvent = Event,
        string canonical = Canonical)
    {
        await Apply(factory, ActivityRecords.AlertChange("c-1", "opened", sampleEvent: sampleEvent));
        await client.PostAsync("/api/runbooks", Json(Upload));
        await client.PutAsync("/api/runbooks/settlement-timeout", Json(canonical));

        var accept = new HttpRequestMessage(HttpMethod.Post, "/api/runbooks/settlement-timeout/accept?version=2");
        accept.Headers.Add("X-User", "anna");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(accept)).StatusCode);
    }

    private static async Task<HttpResponseMessage> Start(
        HttpClient client,
        string user,
        string? runbookId = "settlement-timeout",
        string alertId = "alert-1")
    {
        var body = runbookId is null ? "{}" : $$"""{"runbook_id": "{{runbookId}}"}""";

        return await Send(client, $"/api/alerts/{alertId}/runs", user, Json(body));
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, string url, string user)
    {
        return Send(client, url, user, null);
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string url, string user, HttpContent? content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Add("X-User", user);

        return await client.SendAsync(request);
    }

    private static StringContent Json(string json)
    {
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static async Task Apply(WebApplicationFactory<Program> factory, string record)
    {
        await using var scope = factory.Services.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IActivityRecordHandler>().HandleAsync(Topics.AlertsChanges, record);
    }
}
