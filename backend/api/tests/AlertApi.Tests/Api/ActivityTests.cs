using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using AlertApi.Application.Constants;
using AlertApi.Application.Exceptions;
using AlertApi.Application.Interfaces;
using AlertApi.Infrastructure.Data;
using AlertApi.Tests.Infrastructure;

namespace AlertApi.Tests.Api;

[Trait("Category", "Integration")]
public sealed class ActivityTests : IClassFixture<PostgresFixture>
{
    private const string Mapping = """
        {"id": "trade-v1", "status": "active", "origin": "llm", "priority": 100,
         "match": {"all": [{"field": "raw.meta.schema", "op": "glob", "value": "trade*"}]},
         "fields": {"type": {"path": "meta.schema"}}}
        """;

    private readonly PostgresFixture _postgres;

    public ActivityTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task AlertChange_CreatesTheAlertAndOneTimelineRow_AndAppliesARepeatOnce()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();

        await Handle(factory, Topics.AlertsChanges, ActivityRecords.AlertChange("c-1", "opened"));
        await Handle(factory, Topics.AlertsChanges, ActivityRecords.AlertChange("c-1", "opened"));
        var detail = await client.GetFromJsonAsync<JsonElement>("/api/alerts/alert-1");

        var alert = detail.GetProperty("alert");
        Assert.Equal("settlements", alert.GetProperty("team_id").GetString());
        Assert.Equal("TIMEOUT", alert.GetProperty("fields").GetProperty("error.code").GetString());
        Assert.Equal("2026-11-20T10:15:01.000Z", alert.GetProperty("first_seen").GetString());
        Assert.Equal(JsonValueKind.Null, alert.GetProperty("incident_id").ValueKind);
        var row = Assert.Single(detail.GetProperty("timeline").EnumerateArray());
        Assert.Equal("opened", row.GetProperty("kind").GetString());
        Assert.Equal("c-1", row.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("incident").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("runbook").ValueKind);
    }

    [Fact]
    public async Task LaterChanges_ReplaceTheAlert_AndCountThresholdAddsNoTimelineRow()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();

        await Handle(factory, Topics.AlertsChanges, ActivityRecords.AlertChange("c-1", "opened"));
        await Handle(factory, Topics.AlertsChanges, ActivityRecords.AlertChange("c-2", "count_threshold", count: 10));
        await Handle(factory, Topics.AlertsChanges, ActivityRecords.AlertChange("c-3", "acked", status: "acked", count: 10, ackedBy: "anna"));
        var detail = await client.GetFromJsonAsync<JsonElement>("/api/alerts/alert-1");

        var alert = detail.GetProperty("alert");
        Assert.Equal("acked", alert.GetProperty("status").GetString());
        Assert.Equal("anna", alert.GetProperty("acked_by").GetString());
        Assert.Equal(10, alert.GetProperty("count").GetInt64());
        var kinds = detail.GetProperty("timeline").EnumerateArray().Select(row => row.GetProperty("kind").GetString());
        Assert.Equal(["opened", "acked"], kinds);
    }

    [Fact]
    public async Task GetAlerts_FiltersByTeamAndStatus_NewestFirst()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await Handle(factory, Topics.AlertsChanges, ActivityRecords.AlertChange("c-1", "opened", "old", firstSeen: "2026-11-20T09:00:00.000Z"));
        await Handle(factory, Topics.AlertsChanges, ActivityRecords.AlertChange("c-2", "opened", "new", firstSeen: "2026-11-20T10:00:00.000Z"));
        await Handle(factory, Topics.AlertsChanges, ActivityRecords.AlertChange("c-3", "opened", "pricing-1", team: "pricing"));
        await Handle(factory, Topics.AlertsChanges, ActivityRecords.AlertChange("c-4", "acked", "done", status: "acked"));

        var settlements = await client.GetFromJsonAsync<JsonElement>("/api/alerts?team=settlements");
        var open = await client.GetFromJsonAsync<JsonElement>("/api/alerts?team=settlements&status=open&limit=1");

        var ids = settlements.EnumerateArray().Select(alert => alert.GetProperty("id").GetString());
        Assert.Equal(["done", "new", "old"], ids);
        Assert.Equal("new", Assert.Single(open.EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetAlert_ForAnUnknownId_Returns404()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/alerts/nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("not_found", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task TimelineEntry_StoresTheRowAndTheNotification_AndUpdatesItOnALaterEntry()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await Handle(factory, Topics.AlertsChanges, ActivityRecords.AlertChange("c-1", "opened"));

        await Handle(factory, Topics.AlertsTimeline, ActivityRecords.Timeline("e-1", "notification_failed", "failed"));
        await Handle(factory, Topics.AlertsTimeline, ActivityRecords.Timeline("e-1", "notification_failed", "failed"));
        await Handle(factory, Topics.AlertsTimeline, ActivityRecords.Timeline("e-2"));
        var detail = await client.GetFromJsonAsync<JsonElement>("/api/alerts/alert-1");

        var notification = Assert.Single(detail.GetProperty("notifications").EnumerateArray());
        Assert.Equal("sent", notification.GetProperty("status").GetString());
        Assert.Equal("marek", notification.GetProperty("user_id").GetString());
        Assert.Equal("2026-11-20T10:15:03.410Z", notification.GetProperty("sent_at").GetString());
        var kinds = detail.GetProperty("timeline").EnumerateArray().Select(row => row.GetProperty("kind").GetString());
        Assert.Equal(["opened", "notification_failed", "notification_sent"], kinds);
        var data = detail.GetProperty("timeline")[1].GetProperty("data").GetProperty("notification");
        Assert.Equal("sms", data.GetProperty("channel").GetString());
    }

    [Fact]
    public async Task RecordThatCannotBeApplied_IsRejectedAsInvalid_AndStoresNothing()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);

        await Assert.ThrowsAsync<InvalidRecordException>(() => Handle(factory, Topics.AlertsChanges, "not json"));
        await Assert.ThrowsAsync<InvalidRecordException>(() => Handle(factory, Topics.AlertsChanges, """{"change_id": "c-1"}"""));
        await Assert.ThrowsAsync<InvalidRecordException>(() => Handle(factory, Topics.AlertsChanges, string.Empty));
        await Assert.ThrowsAsync<InvalidRecordException>(() => Handle(factory, "other.topic", "{}"));

        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<ApiDbContext>().Alerts.AnyAsync());
    }

    [Fact]
    public async Task SchemaSample_IsListedAsPending_AndARepeatDoesNotResetItsStatus()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();

        await Handle(factory, Topics.SchemaSamples, ActivityRecords.SchemaSample());
        await client.PutAsync("/api/schema-samples/fp-1", Json("""{"status": "failed", "error": "no match"}"""));
        await Handle(factory, Topics.SchemaSamples, ActivityRecords.SchemaSample());
        var pending = await client.GetFromJsonAsync<JsonElement>("/api/schema-samples?status=pending");
        var failed = await client.GetFromJsonAsync<JsonElement>("/api/schema-samples?status=failed");

        Assert.Equal(0, pending.GetArrayLength());
        var sample = Assert.Single(failed.EnumerateArray());
        Assert.Equal("no match", sample.GetProperty("error").GetString());
        Assert.Equal("rates", sample.GetProperty("sample").GetProperty("payload").GetProperty("desk").GetString());
    }

    [Fact]
    public async Task PutSchemaSample_WithAnotherStatusOrAnUnknownFingerprint_IsRejected()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await Handle(factory, Topics.SchemaSamples, ActivityRecords.SchemaSample());

        var wrongStatus = await client.PutAsync("/api/schema-samples/fp-1", Json("""{"status": "mapped"}"""));
        var unknown = await client.PutAsync("/api/schema-samples/other", Json("""{"status": "failed"}"""));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongStatus.StatusCode);
        var problem = await wrongStatus.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_schema_sample", problem.GetProperty("error").GetString());
        Assert.Equal("status", problem.GetProperty("details")[0].GetProperty("path").GetString());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task SaveMapping_WithAFingerprint_MarksTheSampleMapped_AndThenItCannotFail()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await Handle(factory, Topics.SchemaSamples, ActivityRecords.SchemaSample());

        var saved = await client.PutAsync("/api/mappings/trade-v1?fingerprint=fp-1", Json(Mapping));
        var mapped = await client.GetFromJsonAsync<JsonElement>("/api/schema-samples?status=mapped");
        var failed = await client.PutAsync("/api/schema-samples/fp-1", Json("""{"status": "failed"}"""));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("fp-1", Assert.Single(mapped.EnumerateArray()).GetProperty("fingerprint").GetString());
        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
    }

    [Fact]
    public async Task SaveMapping_WithAnUnknownFingerprint_Returns404AndSavesNothing()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();

        var response = await client.PutAsync("/api/mappings/trade-v1?fingerprint=missing", Json(Mapping));
        var mappings = await client.GetFromJsonAsync<JsonElement>("/api/mappings");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, mappings.GetArrayLength());
    }

    private static async Task Handle(WebApplicationFactory<Program> factory, string topic, string value)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<IActivityRecordHandler>();

        await handler.HandleAsync(topic, value);
    }

    private static StringContent Json(string body)
    {
        return new StringContent(body, Encoding.UTF8, "application/json");
    }
}
