using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using AlertApi.Infrastructure.Data;
using AlertApi.Tests.Infrastructure;

namespace AlertApi.Tests.Api;

[Trait("Category", "Integration")]
public sealed class RunbookEndpointTests : IClassFixture<PostgresFixture>
{
    private const string Upload = """
        {"team_id": "settlements", "title": "Settlement timeout", "format": "markdown", "source": "1. Retry the workflow."}
        """;

    // What the ai-worker sends after converting the draft (backend/ai-worker/ai_worker/runbooks.py).
    private const string Converted = """
        {"canonical": {"id": "settlement-timeout", "title": "Settlement timeout",
                       "steps": [{"text": "Retry the workflow", "action": "retry_workflow",
                                  "params": {"workflowId": "{{workflowId}}"}, "approval": "required"}]},
         "status": "active"}
        """;

    private readonly PostgresFixture _postgres;

    public RunbookEndpointTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task PostRunbook_IsDraftV1_AndQueuesTheConfigRecord()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();

        var created = await client.PostAsync("/api/runbooks", Json(Upload));
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("settlement-timeout", body.GetProperty("id").GetString());
        Assert.Equal("draft", body.GetProperty("status").GetString());
        Assert.Equal(1, body.GetProperty("version").GetInt32());
        var record = await SingleRecordAsync(factory);
        Assert.Equal("runbook", record.GetProperty("kind").GetString());
        Assert.Equal(
            ["canonical", "format", "id", "source", "status", "team_id", "title", "version"],
            record.GetProperty("body").EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("body").GetProperty("canonical").ValueKind);
    }

    [Fact]
    public async Task PutCanonical_BumpsVersionAndClearsAcceptance()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await client.PostAsync("/api/runbooks", Json(Upload));
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsync("/api/runbooks/settlement-timeout", Json(Converted))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Accept(client, "anna")).StatusCode);

        var again = await client.PutAsync("/api/runbooks/settlement-timeout", Json(Converted));
        var body = await again.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(3, body.GetProperty("version").GetInt32());
        Assert.Equal("active", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("accepted_by").ValueKind);
    }

    [Fact]
    public async Task Accept_NeedsAPersonAndAnActiveRunbook()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await client.PostAsync("/api/runbooks", Json(Upload));

        var draft = await Accept(client, "anna");
        await client.PutAsync("/api/runbooks/settlement-timeout", Json(Converted));
        var service = await Accept(client, "ai-worker");
        var stale = await Accept(client, "anna", version: 1);
        var person = await Accept(client, "anna");

        Assert.Equal(HttpStatusCode.Conflict, draft.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, service.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(HttpStatusCode.OK, person.StatusCode);
        var body = await person.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("anna", body.GetProperty("accepted_by").GetString());
        Assert.Equal(2, body.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task UnknownId_Is404_AndInvalidCanonical_Is422WithDetails()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await client.PostAsync("/api/runbooks", Json(Upload));

        var unknown = await client.GetAsync("/api/runbooks/nope");
        var invalid = await client.PutAsync("/api/runbooks/settlement-timeout", Json(Converted.Replace("retry_workflow", "reboot")));
        var body = await invalid.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal("invalid_runbook", body.GetProperty("error").GetString());
        Assert.Equal("canonical.steps[0].action", body.GetProperty("details")[0].GetProperty("path").GetString());
    }

    private static async Task<JsonElement> SingleRecordAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var record = await scope.ServiceProvider.GetRequiredService<ApiDbContext>().Outbox
            .SingleAsync(message => message.Key == "runbook:settlement-timeout");

        return JsonDocument.Parse(record.Payload).RootElement;
    }

    private static Task<HttpResponseMessage> Accept(HttpClient client, string user, int version = 2)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/runbooks/settlement-timeout/accept?version={version}");
        request.Headers.Add("X-User", user);

        return client.SendAsync(request);
    }

    private static StringContent Json(string json)
    {
        return new StringContent(json, System.Text.Encoding.UTF8, "application/json");
    }
}
