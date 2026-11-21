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

// An alert whose rule names no runbook gets the team's first active runbook (by id) whose `matches` fits its event.
[Trait("Category", "Integration")]
public sealed class RunbookMatchTests : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-20T10:15:10Z");

    private const string Timeout = """
        {"type": "WORKFLOW_FAILED", "env": "prod", "mapped": true, "severity": "critical", "raw": {"error": {"code": "TIMEOUT"}}}
        """;

    private const string Rejected = """
        {"type": "WORKFLOW_FAILED", "env": "prod", "mapped": true, "severity": "critical", "raw": {"error": {"code": "REJECTED"}}}
        """;

    private const string MatchesTimeout = """
        {"all": [{"field": "type", "op": "eq", "value": "WORKFLOW_FAILED"}, {"field": "error.code", "op": "eq", "value": "TIMEOUT"}]}
        """;

    private readonly PostgresFixture _postgres;

    public RunbookMatchTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task Opened_WithoutARuleRunbook_StartsTheRunbookWhoseMatchesFits()
    {
        var (factory, client) = Prepare();
        using var _f = factory;
        using var _c = client;
        await AddRunbook(client, "settlements", "Timeout health", MatchesTimeout);

        await Open(factory, Timeout);

        Assert.Equal("timeout-health", await RunbookOfLatestRun(client));
    }

    [Fact]
    public async Task Opened_WhenNothingMatches_StartsNothing()
    {
        var (factory, client) = Prepare();
        using var _f = factory;
        using var _c = client;
        await AddRunbook(client, "settlements", "Timeout health", MatchesTimeout);

        await Open(factory, Rejected);

        Assert.Null(await RunbookOfLatestRun(client));
    }

    [Fact]
    public async Task Opened_WithSeveralMatches_TakesTheFirstById()
    {
        var (factory, client) = Prepare();
        using var _f = factory;
        using var _c = client;
        await AddRunbook(client, "settlements", "Zulu health", MatchesTimeout);
        await AddRunbook(client, "settlements", "Alpha health", MatchesTimeout);

        await Open(factory, Timeout);

        Assert.Equal("alpha-health", await RunbookOfLatestRun(client));
    }

    [Fact]
    public async Task Opened_WithARuleRunbook_UsesItInsteadOfAMatch()
    {
        var (factory, client) = Prepare();
        using var _f = factory;
        using var _c = client;
        await AddRunbook(client, "settlements", "Alpha health", MatchesTimeout);
        await AddRunbook(client, "settlements", "Named health", matches: null);

        await Open(factory, Timeout, runbookId: "named-health");

        Assert.Equal("named-health", await RunbookOfLatestRun(client));
    }

    [Fact]
    public async Task Opened_WithARuleRunbookThatCannotRun_DoesNotFallBackToAMatch()
    {
        var (factory, client) = Prepare();
        using var _f = factory;
        using var _c = client;
        await AddRunbook(client, "settlements", "Alpha health", MatchesTimeout);

        await Open(factory, Timeout, runbookId: "missing-runbook");

        Assert.Null(await RunbookOfLatestRun(client));
    }

    [Fact]
    public async Task Opened_IgnoresAnotherTeamsRunbook_ADraft_AndARunbookWithoutMatches()
    {
        var (factory, client) = Prepare();
        using var _f = factory;
        using var _c = client;
        await AddRunbook(client, "pricing", "Pricing health", MatchesTimeout);
        await AddRunbook(client, "settlements", "No matches health", matches: null);
        await client.PostAsync("/api/runbooks", Json("""{"team_id": "settlements", "title": "Draft health", "format": "markdown", "source": "1. Look."}"""));

        await Open(factory, Timeout);

        Assert.Null(await RunbookOfLatestRun(client));
    }

    private (WebApplicationFactory<Program> Factory, HttpClient Client) Prepare()
    {
        var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false, clock: new TestClock(Start));

        return (factory, factory.CreateClient());
    }

    // A read-only runbook, so a match starts at once without a runbook_starts row.
    private static async Task AddRunbook(HttpClient client, string team, string title, string? matches)
    {
        var id = title.ToLowerInvariant().Replace(' ', '-');
        var matchesPart = matches is null ? string.Empty : $"\"matches\": {matches},";

        await client.PostAsync("/api/runbooks", Json($$$"""{"team_id": "{{{team}}}", "title": "{{{title}}}", "format": "markdown", "source": "1. Look."}"""));
        var put = await client.PutAsync($"/api/runbooks/{id}", Json($$$"""
            {"canonical": {"id": "{{{id}}}", "title": "{{{title}}}", {{{matchesPart}}}
             "steps": [{"id": "health", "text": "Check", "action": "http_get", "params": {"url": "http://counterparty-service/health"}}]},
             "status": "active"}
            """));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var accept = new HttpRequestMessage(HttpMethod.Post, $"/api/runbooks/{id}/accept?version=2");
        accept.Headers.Add("X-User", "anna");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(accept)).StatusCode);
    }

    private static async Task Open(WebApplicationFactory<Program> factory, string sampleEvent, string? runbookId = null)
    {
        using var scope = factory.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<IActivityRecordHandler>().HandleAsync(
            Topics.AlertsChanges,
            ActivityRecords.AlertChange("c-1", "opened", sampleEvent: sampleEvent, runbookId: runbookId));
    }

    private static async Task<string?> RunbookOfLatestRun(HttpClient client)
    {
        var alert = await client.GetFromJsonAsync<JsonElement>("/api/alerts/alert-1");
        var progress = alert.GetProperty("runbook");

        return progress.ValueKind == JsonValueKind.Null ? null : progress.GetProperty("runbook_id").GetString();
    }

    private static StringContent Json(string body)
    {
        return new StringContent(body, Encoding.UTF8, "application/json");
    }
}
