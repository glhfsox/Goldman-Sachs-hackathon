using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using AlertApi.Application.Constants;
using AlertApi.Application.Interfaces;
using AlertApi.Infrastructure.Actions;
using AlertApi.Infrastructure.Data;
using AlertApi.Tests.Infrastructure;

namespace AlertApi.Tests.Api;

[Trait("Category", "Integration")]
public sealed class RunExecutorTests : IClassFixture<PostgresFixture>
{
    private const string Event = """
        {"type": "WORKFLOW_FAILED", "env": "prod", "mapped": true, "raw": {"workflowId": "WF-17"}}
        """;

    private const string Upload = """
        {"team_id": "settlements", "title": "Settlement timeout", "format": "markdown", "source": "1. Retry."}
        """;

    // A health check, a retry that may run twice, an escalation only if the retry failed, and a note for a person.
    private const string Canonical = """
        {"canonical": {"id": "settlement-timeout", "title": "Settlement timeout",
         "steps": [
          {"id": "health", "text": "Is counterparty-service healthy?", "action": "http_get",
           "params": {"url": "http://counterparty-service/health"}},
          {"id": "retry", "text": "Retry {{workflowId}}", "action": "retry_workflow", "needs": ["health"],
           "params": {"workflowId": "{{workflowId}}"}, "retries": 1},
          {"id": "escalate", "text": "Escalate", "action": "escalate", "needs": ["retry"],
           "if": {"step": "retry", "status": "failed"}, "params": {"team": "counterparty-support"}}
          ]},
         "status": "active"}
        """;

    private readonly PostgresFixture _postgres;

    public RunExecutorTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task Run_WhenEveryCallSucceeds_RunsTheStepsInOrder_AndSkipsTheBranchNotTaken()
    {
        var calls = new Calls();
        using var factory = Create(calls);
        using var client = factory.CreateClient();
        await Prepare(factory, client);
        var runId = await StartRun(client);

        await Drain(factory);

        var run = await client.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}");
        var nodes = Nodes(run);
        var detail = await client.GetFromJsonAsync<JsonElement>("/api/alerts/alert-1");
        var kinds = detail.GetProperty("timeline").EnumerateArray().Select(entry => entry.GetProperty("kind").GetString()).ToList();

        Assert.Equal("done", run.GetProperty("status").GetString());
        Assert.Equal("succeeded", nodes["health"].GetProperty("status").GetString());
        Assert.Equal("succeeded", nodes["retry"].GetProperty("status").GetString());
        Assert.Equal("skipped", nodes["escalate"].GetProperty("status").GetString());
        Assert.Equal(1, nodes["retry"].GetProperty("attempt").GetInt32());
        Assert.True(nodes["retry"].GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.Equal([$"{runId}:health", $"{runId}:retry"], calls.Keys);
        Assert.Equal(2, kinds.Count(kind => kind == "action_finished"));
        Assert.Contains("run_done", kinds);
    }

    [Fact]
    public async Task Run_WhenAStepFails_RetriesItOnce_ThenTakesTheBranchAndEndsFailed()
    {
        var calls = new Calls { Fail = request => request.Contains("workflow") };
        using var factory = Create(calls);
        using var client = factory.CreateClient();
        await Prepare(factory, client);
        var runId = await StartRun(client);

        await Drain(factory);
        var afterFirst = Nodes(await client.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}"));

        // The planner backs off before the retry; the test skips the wait.
        await MakeDue(factory, runId);
        await Drain(factory);

        var run = await client.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}");
        var nodes = Nodes(run);
        var detail = await client.GetFromJsonAsync<JsonElement>("/api/alerts/alert-1");
        var finished = detail.GetProperty("timeline").EnumerateArray()
            .Where(entry => entry.GetProperty("kind").GetString() == "action_finished")
            .Select(entry => entry.GetProperty("data"))
            .Where(data => data.GetProperty("node_id").GetString() == "retry")
            .ToList();

        Assert.Equal("ready", afterFirst["retry"].GetProperty("status").GetString());
        Assert.Equal("failed", nodes["retry"].GetProperty("status").GetString());
        Assert.Equal(2, nodes["retry"].GetProperty("attempt").GetInt32());
        Assert.Contains("HTTP 500", nodes["retry"].GetProperty("result").GetProperty("error").GetString());
        Assert.Equal("succeeded", nodes["escalate"].GetProperty("status").GetString());
        Assert.Equal("failed", run.GetProperty("status").GetString());
        Assert.Equal([1, 2], finished.Select(data => data.GetProperty("attempt").GetInt32()));
        Assert.Equal("failed", finished[1].GetProperty("action_run").GetProperty("status").GetString());
        Assert.Equal(2, calls.Keys.Count(key => key == $"{runId}:retry"));
    }

    [Fact]
    public async Task Run_WhenAPluginThrows_TheStepFails_AndTheExecutorKeepsWorking()
    {
        var calls = new Calls { Throw = request => request.Contains("health") };
        using var factory = Create(calls);
        using var client = factory.CreateClient();
        await Prepare(factory, client);
        var runId = await StartRun(client);

        await Drain(factory);

        var run = await client.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}");
        var nodes = Nodes(run);

        Assert.Equal("failed", nodes["health"].GetProperty("status").GetString());
        Assert.Contains("failed", nodes["health"].GetProperty("result").GetProperty("error").GetString());
        Assert.Equal("blocked", nodes["retry"].GetProperty("status").GetString());
        Assert.Equal("failed", run.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Run_WhenReplicasWorkTogether_EveryStepIsCalledExactlyOnce()
    {
        var calls = new Calls();
        using var factory = Create(calls);
        using var client = factory.CreateClient();
        var steps = string.Join(",", Enumerable.Range(1, 12).Select(index => $$"""
            {"id": "check{{index}}", "text": "Check {{index}}", "action": "http_get", "params": {"url": "http://counterparty-service/health{{index}}"} }
            """));
        await Prepare(factory, client, canonical: "{\"canonical\": {\"id\": \"settlement-timeout\", \"title\": \"Fan out\", \"steps\": [" + steps + "]}, \"status\": \"active\"}");
        var runId = await StartRun(client);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Drain(factory)));

        var run = await client.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}");

        Assert.Equal("done", run.GetProperty("status").GetString());
        Assert.Equal(12, calls.Keys.Count);
        Assert.Equal(12, calls.Keys.Distinct().Count());
    }

    [Fact]
    public async Task Run_WhenAWorkerDiesMidStep_TheNodeIsRecoveredAsFailed_AfterItsLeaseRunsOut()
    {
        var calls = new Calls();
        using var factory = Create(calls);
        using var client = factory.CreateClient();
        await Prepare(factory, client);
        var runId = await StartRun(client);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
            // A worker that claimed the node and then died: running, attempt counted, lease in the past.
            await context.ActionRuns
                .Where(node => node.RunId == runId && node.NodeId == "health")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(node => node.Status, "running")
                    .SetProperty(node => node.Attempt, 1)
                    .SetProperty(node => node.LeaseUntil, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        await Drain(factory);

        var nodes = Nodes(await client.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}"));

        Assert.Equal("failed", nodes["health"].GetProperty("status").GetString());
        Assert.Contains("stopped", nodes["health"].GetProperty("result").GetProperty("error").GetString());
        Assert.Equal("blocked", nodes["retry"].GetProperty("status").GetString());
        Assert.Empty(calls.Keys);
    }

    [Fact]
    public async Task Run_WhenCancelledWhileAStepIsRunning_KeepsThatStepsResult_AndMovesNothingOn()
    {
        var gate = new TaskCompletionSource();
        var calls = new Calls { Gate = gate.Task };
        using var factory = Create(calls);
        using var client = factory.CreateClient();
        await Prepare(factory, client);
        var runId = await StartRun(client);

        var working = Drain(factory);
        await calls.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var cancel = new HttpRequestMessage(HttpMethod.Post, $"/api/runs/{runId}/cancel");
        cancel.Headers.Add("X-User", "anna");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(cancel)).StatusCode);
        gate.SetResult();
        await working;

        var run = await client.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}");
        var nodes = Nodes(run);

        Assert.Equal("cancelled", run.GetProperty("status").GetString());
        Assert.Equal("succeeded", nodes["health"].GetProperty("status").GetString());
        Assert.Equal("cancelled", nodes["retry"].GetProperty("status").GetString());
        Assert.Single(calls.Keys);
    }

    [Fact]
    public async Task Run_WithTheKillSwitchOff_NoWorkerStarts()
    {
        var calls = new Calls();
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false, runExecutor: true)
            .WithWebHostBuilder(host => host.UseSetting("RUNBOOK_EXECUTION_ENABLED", "false"));
        using var client = factory.CreateClient();
        await Prepare(factory, client);
        var runId = await StartRun(client);

        await Task.Delay(500);

        var nodes = Nodes(await client.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}"));

        Assert.Equal("ready", nodes["health"].GetProperty("status").GetString());
        Assert.Empty(calls.Keys);
    }

    private WebApplicationFactory<Program> Create(Calls calls)
    {
        return ApiFactory.Create(_postgres, "Production", enableSwagger: false).WithWebHostBuilder(host =>
            host.ConfigureServices(services => services
                .AddHttpClient(HttpTargetAction.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => calls)));
    }

    // What one replica's workers would do: take steps until there is nothing due.
    private static async Task Drain(WebApplicationFactory<Program> factory)
    {
        while (true)
        {
            await using var scope = factory.Services.CreateAsyncScope();

            if (!await scope.ServiceProvider.GetRequiredService<IRunExecutor>().ExecuteNextAsync())
            {
                return;
            }
        }
    }

    private static async Task MakeDue(WebApplicationFactory<Program> factory, string runId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ApiDbContext>();

        await context.ActionRuns
            .Where(node => node.RunId == runId && node.Status == "ready")
            .ExecuteUpdateAsync(setters => setters.SetProperty(node => node.NotBefore, DateTimeOffset.UtcNow.AddSeconds(-1)));
    }

    private static Dictionary<string, JsonElement> Nodes(JsonElement run)
    {
        return run.GetProperty("nodes").EnumerateArray().ToDictionary(node => node.GetProperty("id").GetString()!);
    }

    private static async Task<string> StartRun(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/alerts/alert-1/runs")
        {
            Content = Json("""{"runbook_id": "settlement-timeout"}""")
        };
        request.Headers.Add("X-User", "anna");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task Prepare(WebApplicationFactory<Program> factory, HttpClient client, string canonical = Canonical)
    {
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IActivityRecordHandler>()
                .HandleAsync(Topics.AlertsChanges, ActivityRecords.AlertChange("c-1", "opened", sampleEvent: Event));
        }

        await client.PostAsync("/api/runbooks", Json(Upload));
        await client.PutAsync("/api/runbooks/settlement-timeout", Json(canonical));

        var accept = new HttpRequestMessage(HttpMethod.Post, "/api/runbooks/settlement-timeout/accept?version=2");
        accept.Headers.Add("X-User", "anna");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(accept)).StatusCode);
    }

    private static StringContent Json(string json)
    {
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    // Stands in for mock-target: records the Idempotency-Key of every call and answers as the test says.
    private sealed class Calls : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _keys = new();

        public Func<string, bool> Fail { get; init; } = _ => false;

        public Func<string, bool> Throw { get; init; } = _ => false;

        public Task? Gate { get; init; }

        public TaskCompletionSource Started { get; } = new();

        public IReadOnlyList<string> Keys => _keys.ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            if (Throw(url))
            {
                throw new InvalidOperationException("the plugin blew up");
            }

            _keys.Enqueue(request.Headers.GetValues("Idempotency-Key").Single());
            Started.TrySetResult();

            if (Gate is not null)
            {
                await Gate;
            }

            return new HttpResponseMessage(Fail(url) ? HttpStatusCode.InternalServerError : HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
        }
    }
}
