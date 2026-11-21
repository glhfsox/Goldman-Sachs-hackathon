using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using AlertApi.Application.Exceptions;
using AlertApi.Application.Interfaces;
using AlertApi.Infrastructure.Data;
using AlertApi.Tests.Infrastructure;

namespace AlertApi.Tests.Api;

[Trait("Category", "Integration")]
public sealed class ActionTargetEndpointTests : IClassFixture<PostgresFixture>
{
    private const string Upload = """
        {"team_id": "settlements", "title": "Settlement timeout", "format": "yaml", "source": "steps: ..."}
        """;

    // The ai-worker's conversion of Appendix B's YAML: no targets, a URL for the health check.
    private const string Converted = """
        {"canonical": {"id": "settlement-timeout", "title": "Settlement timeout",
                       "steps": [{"text": "Is counterparty-service healthy?", "action": "http_get",
                                  "params": {"url": "http://counterparty-service/health"}, "approval": "none"},
                                 {"text": "Retry the workflow", "action": "retry_workflow",
                                  "params": {"workflowId": "{{workflowId}}"}, "approval": "none"},
                                 {"text": "Escalate", "action": "escalate", "params": {"team": "counterparty-support"},
                                  "when": "retry fails twice", "approval": "required"}]},
         "status": "active"}
        """;

    private readonly PostgresFixture _postgres;

    public ActionTargetEndpointTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task Actions_AndSeededTargets_AreListed()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();

        var actions = await client.GetFromJsonAsync<JsonElement>("/api/actions");
        var targets = await client.GetFromJsonAsync<JsonElement>("/api/action-targets");

        Assert.Equal(
            ["escalate", "http_get", "reload_db", "restart_service", "retry_workflow", "ticket", "webhook"],
            actions.EnumerateArray().Select(action => action.GetProperty("name").GetString()));
        Assert.Equal("auto", actions.EnumerateArray().Single(action => action.GetProperty("name").GetString() == "retry_workflow")
            .GetProperty("risk").GetString());
        Assert.Contains(targets.EnumerateArray(), target => target.GetProperty("id").GetString() == "counterparty-service"
            && target.GetProperty("origins")[0].GetString() == "http://counterparty-service");
    }

    [Fact]
    public async Task AiWorkerConversionWithoutTargets_IsAccepted()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await client.PostAsync("/api/runbooks", Json(Upload));

        var saved = await client.PutAsync("/api/runbooks/settlement-timeout", Json(Converted));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
    }

    [Fact]
    public async Task PutTarget_CreatesThenBumpsVersion_AndRejectsAnInvalidOne()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        const string target = """
            {"base_url": "http://fx-engine:9000/api", "origins": ["http://fx-engine"],
             "operations": {"retry_workflow": {"method": "POST", "path": "/jobs/{workflowId}/rerun"}}}
            """;

        var created = await client.PutAsync("/api/action-targets/fx-engine", Json(target));
        var replaced = await client.PutAsync("/api/action-targets/fx-engine", Json(target));
        var invalid = await client.PutAsync("/api/action-targets/fx-engine", Json(target.Replace("\"POST\"", "\"PATCH\"")));

        Assert.Equal(1, (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt32());
        Assert.Equal(2, (await replaced.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal("invalid_action_target", (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task PutTarget_ThatWouldBreakAnActiveRunbook_Is409()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await client.PostAsync("/api/runbooks", Json(Upload));
        await client.PutAsync("/api/runbooks/settlement-timeout", Json(Converted));

        var dropped = await client.PutAsync("/api/action-targets/counterparty-service", Json("""
            {"base_url": "http://mock-target:8003/counterparty-service", "origins": [],
             "operations": {"http_get": {"method": "GET", "path": "/health"}}}
            """));
        var body = await dropped.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Conflict, dropped.StatusCode);
        Assert.Contains("settlement-timeout", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task AcceptSavedAfterAConcurrentPut_IsAConflict()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await client.PostAsync("/api/runbooks", Json(Upload));
        await client.PutAsync("/api/runbooks/settlement-timeout", Json(Converted));
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
        var stale = await context.Runbooks.SingleAsync(runbook => runbook.Id == "settlement-timeout");

        await client.PutAsync("/api/runbooks/settlement-timeout", Json(Converted));
        stale.Accept("anna", DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<ConcurrentUpdateException>(() => ((IUnitOfWork)context).SaveChangesAsync());
    }

    private static StringContent Json(string json)
    {
        return new StringContent(json, System.Text.Encoding.UTF8, "application/json");
    }
}
