using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using AlertApi.Application.Constants;
using AlertApi.Application.Interfaces;
using AlertApi.Infrastructure.Data;
using AlertApi.Tests.Infrastructure;

namespace AlertApi.Tests.Api;

[Trait("Category", "Integration")]
public sealed class ProposalTests : IClassFixture<PostgresFixture>
{
    private const string RuleSeverity = """
        {"kind": "rule_severity", "body": {"rule_id": "settlements-workflow-failed", "team_id": "settlements",
         "severity": "critical", "current_severity": null, "confidence": 0.82, "mode": "first", "stats": null,
         "reasons": ["it comes from production"]}}
        """;

    private const string RunbookProposal = """
        {"kind": "runbook", "body": {"team_id": "settlements", "title": "WORKFLOW_FAILED in prod",
         "canonical": {"id": "workflow-failed-prod", "title": "WORKFLOW_FAILED in prod",
          "matches": {"all": [{"field": "type", "op": "eq", "value": "WORKFLOW_FAILED"}]},
          "steps": [{"text": "Check counterparty-service health", "action": "http_get",
                     "params": {"url": "http://counterparty-service/health"}, "approval": "none"}]}}}
        """;

    private const string RunbookStart = """
        {"kind": "runbook_start", "body": {"rule_id": "settlements-workflow-failed", "team_id": "settlements",
         "runbook_id": "settlement-timeout", "start": "auto", "current_start": "approval", "confidence": 0.66}}
        """;

    private readonly PostgresFixture _postgres;

    public ProposalTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task Create_ByTheAiWorker_Returns201_ListsItAndWritesATimelineRowAndAUiMessage()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await Apply(factory, ActivityRecords.AlertChange("c-1", "opened"));

        var created = await Send(client, "/api/alerts/alert-1/proposals", "ai-worker", RuleSeverity);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var open = await client.GetFromJsonAsync<JsonElement>("/api/proposals?status=open");
        var detail = await client.GetFromJsonAsync<JsonElement>("/api/alerts/alert-1");
        var byId = await client.GetFromJsonAsync<JsonElement>($"/api/proposals/{body.GetProperty("id").GetString()}");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("open", body.GetProperty("status").GetString());
        Assert.Equal("ai-worker", body.GetProperty("author").GetString());
        Assert.Equal("critical", body.GetProperty("body").GetProperty("severity").GetString());
        Assert.Equal(body.GetProperty("id").GetString(), Assert.Single(open.EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal("rule_severity", byId.GetProperty("kind").GetString());
        Assert.Contains(detail.GetProperty("timeline").EnumerateArray(), row => row.GetProperty("kind").GetString() == "proposal_created");
        // One message for the alert's own opened row, one for the proposal.
        Assert.Equal(["alert-1", "alert-1"], factory.Services.GetRequiredService<RecordingUiPublisher>().Timelines);
    }

    [Fact]
    public async Task Create_TheSameOpenProposalTwice_ReturnsTheFirstWith200()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await Apply(factory, ActivityRecords.AlertChange("c-1", "opened"));

        var first = await Send(client, "/api/alerts/alert-1/proposals", "ai-worker", RuleSeverity);
        var second = await Send(client, "/api/alerts/alert-1/proposals", "ai-worker", RuleSeverity.Replace("\"critical\"", "\"critical\"  "));
        var all = await client.GetFromJsonAsync<JsonElement>("/api/proposals");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(
            (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString(),
            (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString());
        Assert.Equal(1, all.GetArrayLength());
    }

    [Fact]
    public async Task Create_WithTheSameIdempotencyKey_ReturnsTheFirstWith200_WhateverTheBody()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await Apply(factory, ActivityRecords.AlertChange("c-1", "opened"));

        var first = await Send(client, "/api/alerts/alert-1/proposals", "ai-worker", RuleSeverity, "severity:r1:first");
        var second = await Send(client, "/api/alerts/alert-1/proposals", "ai-worker", RuleSeverity.Replace("0.82", "0.79"), "severity:r1:first");
        var other = await Send(client, "/api/alerts/alert-1/proposals", "ai-worker", RuleSeverity.Replace("0.82", "0.7"), "severity:r1:20");
        var all = await client.GetFromJsonAsync<JsonElement>("/api/proposals");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
        Assert.Equal(
            (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString(),
            (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString());
        Assert.Equal(2, all.GetArrayLength());
    }

    [Fact]
    public async Task Create_WithABadRequest_IsRefusedWithThePath()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await Apply(factory, ActivityRecords.AlertChange("c-1", "opened"));

        var unknownKind = await Send(client, "/api/alerts/alert-1/proposals", "ai-worker", """{"kind": "magic", "body": {}}""");
        var badSeverity = await Send(client, "/api/alerts/alert-1/proposals", "ai-worker", RuleSeverity.Replace("\"critical\"", "\"urgent\""));
        var badCanonical = await Send(client, "/api/alerts/alert-1/proposals", "ai-worker", RunbookProposal.Replace("\"http_get\"", "\"rm_rf\""));
        var noUser = await Send(client, "/api/alerts/alert-1/proposals", null, RuleSeverity);
        var stranger = await Send(client, "/api/alerts/alert-1/proposals", "nobody", RuleSeverity);
        var noAlert = await Send(client, "/api/alerts/nope/proposals", "ai-worker", RuleSeverity);

        Assert.Equal("kind", await FirstPath(unknownKind));
        Assert.Equal("body.severity", await FirstPath(badSeverity));
        Assert.StartsWith("body.canonical.steps[0]", await FirstPath(badCanonical));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noUser.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noAlert.StatusCode);
    }

    [Fact]
    public async Task Accept_RuleSeverity_SetsTheRulesSeverityAndPublishesItOnConfigChanges()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await Apply(factory, ActivityRecords.AlertChange("c-1", "opened"));
        // settlements-workflow-failed comes with the seed; its record plus the severity change make two.
        var id = await Create(client, RuleSeverity);

        var accepted = await Send(client, $"/api/proposals/{id}/accept", "anna", null);
        var rule = await client.GetFromJsonAsync<JsonElement>("/api/rules/settlements-workflow-failed");
        var again = await Send(client, $"/api/proposals/{id}/accept", "anna", null);
        var rejectDecided = await Send(client, $"/api/proposals/{id}/reject", "anna", null);
        var detail = await client.GetFromJsonAsync<JsonElement>("/api/alerts/alert-1");

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var body = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("accepted", body.GetProperty("status").GetString());
        Assert.Equal("anna", body.GetProperty("decided_by").GetString());
        Assert.Equal("critical", rule.GetProperty("severity").GetString());
        Assert.Equal(2, (await RuleRecords(factory)).Count);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, rejectDecided.StatusCode);
        Assert.Contains(detail.GetProperty("timeline").EnumerateArray(), row => row.GetProperty("kind").GetString() == "proposal_decided");
    }

    [Fact]
    public async Task Accept_ForAServiceOrAnUnknownUser_Is403_AndForAMissingRule409_AndTheProposalStaysOpen()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await Apply(factory, ActivityRecords.AlertChange("c-1", "opened"));
        var id = await Create(client, RuleSeverity.Replace("settlements-workflow-failed", "deleted-rule"));

        var service = await Send(client, $"/api/proposals/{id}/accept", "ai-worker", null);
        var noUser = await Send(client, $"/api/proposals/{id}/reject", null, null);
        var missingRule = await Send(client, $"/api/proposals/{id}/accept", "anna", null);
        var unknown = await Send(client, "/api/proposals/nope/accept", "anna", null);
        var stillOpen = await client.GetFromJsonAsync<JsonElement>($"/api/proposals/{id}");

        Assert.Equal(HttpStatusCode.Forbidden, service.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, noUser.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, missingRule.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("open", stillOpen.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Accept_Runbook_SavesAnActiveAcceptedRunbookWithItsOwnId()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await Apply(factory, ActivityRecords.AlertChange("c-1", "opened"));
        var id = await Create(client, RunbookProposal);

        var accepted = await Send(client, $"/api/proposals/{id}/accept", "anna", null);
        var runbooks = await client.GetFromJsonAsync<JsonElement>("/api/runbooks?team=settlements");

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var runbook = Assert.Single(runbooks.EnumerateArray());
        Assert.Equal("workflow_failed-in-prod".Replace('_', '-'), runbook.GetProperty("id").GetString());
        Assert.Equal("active", runbook.GetProperty("status").GetString());
        Assert.Equal("anna", runbook.GetProperty("accepted_by").GetString());
        Assert.Equal(runbook.GetProperty("id").GetString(), runbook.GetProperty("canonical").GetProperty("id").GetString());
        Assert.Equal("http_get", runbook.GetProperty("canonical").GetProperty("steps")[0].GetProperty("action").GetString());
    }

    [Fact]
    public async Task Accept_RunbookStart_Is409UntilRunsExist_ButItCanBeRejected()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await Apply(factory, ActivityRecords.AlertChange("c-1", "opened"));
        var id = await Create(client, RunbookStart);

        var accept = await Send(client, $"/api/proposals/{id}/accept", "anna", null);
        var reject = await Send(client, $"/api/proposals/{id}/reject", "boris", null);

        Assert.Equal(HttpStatusCode.Conflict, accept.StatusCode);
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        var body = await reject.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rejected", body.GetProperty("status").GetString());
        Assert.Equal("boris", body.GetProperty("decided_by").GetString());
    }

    [Fact]
    public async Task Accept_ThresholdAndTemplate_OnlyMarkTheProposalAccepted()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await Apply(factory, ActivityRecords.AlertChange("c-1", "opened"));
        var threshold = await Create(client, """{"kind": "threshold", "body": {"note": "raise the limit to 20,000,000"}}""", "anna");
        var template = await Create(client, """{"kind": "template", "body": {"title": "{{workflowName}} failed"}}""", "anna");

        var first = await Send(client, $"/api/proposals/{threshold}/accept", "boris", null);
        var second = await Send(client, $"/api/proposals/{template}/accept", "boris", null);
        var accepted = await client.GetFromJsonAsync<JsonElement>("/api/proposals?status=accepted");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, accepted.GetArrayLength());
    }

    private static async Task<string> Create(HttpClient client, string json, string author = "ai-worker")
    {
        var response = await Send(client, "/api/alerts/alert-1/proposals", author, json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<string?> FirstPath(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_proposal", body.GetProperty("error").GetString());

        return body.GetProperty("details")[0].GetProperty("path").GetString();
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string path, string? user, string? json, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);

        if (user is not null)
        {
            request.Headers.Add("X-User", user);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (json is not null)
        {
            request.Content = Json(json);
        }

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

    private static async Task<List<string>> RuleRecords(WebApplicationFactory<Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<ApiDbContext>().Outbox
            .Where(message => message.Key == "rule:settlements-workflow-failed")
            .Select(message => message.Payload)
            .ToListAsync();
    }
}
