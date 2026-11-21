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
public sealed class RunbookStartTests : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-20T10:15:10Z");

    private const string Rule = "settlements-workflow-failed";
    private const string Path = "/api/runbook-starts/settlements-workflow-failed/settlement-timeout";

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

    private const string Proposal = """
        {"kind": "runbook_start", "body": {"rule_id": "settlements-workflow-failed", "team_id": "settlements",
         "runbook_id": "settlement-timeout", "start": "auto", "current_start": "approval", "confidence": 0.9}}
        """;

    private readonly PostgresFixture _postgres;

    public RunbookStartTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task Put_ByAPerson_CreatesThenReplacesTheRow_AndGetFiltersIt()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await client.PostAsync("/api/runbooks", Json(Upload));

        var created = await Put(client, "anna", """{"start": "auto"}""");
        var replaced = await Put(client, "boris", """{"start": "approval"}""");
        var all = await client.GetFromJsonAsync<JsonElement>("/api/runbook-starts");
        var byRule = await client.GetFromJsonAsync<JsonElement>($"/api/runbook-starts?rule={Rule}");
        var other = await client.GetFromJsonAsync<JsonElement>("/api/runbook-starts?rule=nope");

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var row = Assert.Single(all.EnumerateArray());
        Assert.Equal("approval", row.GetProperty("start").GetString());
        Assert.Equal("boris", row.GetProperty("set_by").GetString());
        Assert.Single(byRule.EnumerateArray());
        Assert.Empty(other.EnumerateArray());
    }

    [Theory]
    [InlineData("ai-worker", """{"start": "auto"}""", HttpStatusCode.Forbidden)]
    [InlineData(null, """{"start": "auto"}""", HttpStatusCode.Forbidden)]
    [InlineData("anna", """{"start": "sometimes"}""", HttpStatusCode.UnprocessableEntity)]
    [InlineData("anna", """{}""", HttpStatusCode.UnprocessableEntity)]
    public async Task Put_Refused_WritesNothing(string? user, string body, HttpStatusCode expected)
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await client.PostAsync("/api/runbooks", Json(Upload));

        var response = await Put(client, user, body);
        var all = await client.GetFromJsonAsync<JsonElement>("/api/runbook-starts");

        Assert.Equal(expected, response.StatusCode);
        Assert.Empty(all.EnumerateArray());
    }

    [Fact]
    public async Task Put_ForAnUnknownRuleOrRunbook_Is404()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();

        var noRunbook = await Put(client, "anna", """{"start": "auto"}""");
        await client.PostAsync("/api/runbooks", Json(Upload));
        var noRule = await Put(client, "anna", """{"start": "auto"}""", "/api/runbook-starts/no-such-rule/settlement-timeout");

        Assert.Equal(HttpStatusCode.NotFound, noRunbook.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noRule.StatusCode);
    }

    [Fact]
    public async Task AcceptedProposal_MakesTheNextOpenedAlertScheduleAnAutoRun()
    {
        var clock = new TestClock(Start);
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false, clock: clock);
        using var client = factory.CreateClient();
        await client.PostAsync("/api/runbooks", Json(Upload));
        await client.PutAsync("/api/runbooks/settlement-timeout", Json(AutoRunbook));
        await Send(client, "/api/runbooks/settlement-timeout/accept?version=2", "anna", null);
        await Apply(factory, ActivityRecords.AlertChange("c-0", "opened", sampleEvent: Event));
        var proposal = await (await Send(client, "/api/alerts/alert-1/proposals", "ai-worker", Proposal)).Content.ReadFromJsonAsync<JsonElement>();

        var accepted = await Send(client, $"/api/proposals/{proposal.GetProperty("id").GetString()}/accept", "anna", null);
        await Apply(factory, ActivityRecords.AlertChange("c-1", "reopened", sampleEvent: Event));
        var alert = await client.GetFromJsonAsync<JsonElement>("/api/alerts/alert-1");
        var run = await client.GetFromJsonAsync<JsonElement>($"/api/runs/{alert.GetProperty("runbook").GetProperty("run_id").GetString()}");

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("scheduled", run.GetProperty("status").GetString());
        Assert.Equal("auto", run.GetProperty("trigger").GetString());
    }

    private static async Task<HttpResponseMessage> Put(HttpClient client, string? user, string body, string path = Path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = Json(body) };

        if (user is not null)
        {
            request.Headers.Add("X-User", user);
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string path, string user, string? json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-User", user);

        if (json is not null)
        {
            request.Content = Json(json);
        }

        return await client.SendAsync(request);
    }

    private static async Task Apply(WebApplicationFactory<Program> factory, string value)
    {
        await using var scope = factory.Services.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IActivityRecordHandler>().HandleAsync(Topics.AlertsChanges, value);
    }

    private static StringContent Json(string body)
    {
        return new StringContent(body, Encoding.UTF8, "application/json");
    }
}
