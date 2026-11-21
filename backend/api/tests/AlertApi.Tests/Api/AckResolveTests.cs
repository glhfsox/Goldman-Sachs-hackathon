using System.Net;
using System.Net.Http.Json;
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
public sealed class AckResolveTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public AckResolveTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task Ack_ReturnsTheAlertAsPublished_AndQueuesOneChangeRecord()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await Apply(factory, ActivityRecords.AlertChange("c-1", "opened"));

        var response = await Post(client, "/api/alerts/alert-1/ack", "anna");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var alert = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("acked", alert.GetProperty("status").GetString());
        Assert.Equal("anna", alert.GetProperty("acked_by").GetString());
        var record = JsonDocument.Parse(Assert.Single(await QueuedPayloads(factory))).RootElement;
        Assert.Equal("acked", record.GetProperty("change").GetString());
        Assert.Equal("anna", record.GetProperty("actor").GetString());
        Assert.Equal("alert-1", record.GetProperty("alert").GetProperty("id").GetString());
        Assert.Equal("acked", record.GetProperty("alert").GetProperty("status").GetString());
        Assert.True(Guid.TryParse(record.GetProperty("change_id").GetString(), out _));
    }

    [Fact]
    public async Task Ack_TakesEffectInTheCopyWhenTheRecordComesBack_AndASecondAckIsRefused()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await Apply(factory, ActivityRecords.AlertChange("c-1", "opened"));
        await Post(client, "/api/alerts/alert-1/ack", "anna");

        var before = await client.GetFromJsonAsync<JsonElement>("/api/alerts/alert-1");
        await Apply(factory, Assert.Single(await QueuedPayloads(factory)));
        var after = await client.GetFromJsonAsync<JsonElement>("/api/alerts/alert-1");
        var second = await Post(client, "/api/alerts/alert-1/ack", "boris");

        Assert.Equal("open", before.GetProperty("alert").GetProperty("status").GetString());
        Assert.Equal("acked", after.GetProperty("alert").GetProperty("status").GetString());
        var last = after.GetProperty("timeline").EnumerateArray().Last();
        Assert.Equal("acked", last.GetProperty("kind").GetString());
        Assert.Equal("anna", last.GetProperty("actor").GetString());
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Resolve_WorksOnOpenAndAckedAlerts_ButNotOnAResolvedOne()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await Apply(factory, ActivityRecords.AlertChange("c-1", "opened", "open-1"));
        await Apply(factory, ActivityRecords.AlertChange("c-2", "acked", "acked-1", status: "acked", ackedBy: "anna"));
        await Apply(factory, ActivityRecords.AlertChange("c-3", "resolved", "done-1", status: "resolved"));

        var fromOpen = await Post(client, "/api/alerts/open-1/resolve", "boris");
        var fromAcked = await Post(client, "/api/alerts/acked-1/resolve", "boris");
        var fromResolved = await Post(client, "/api/alerts/done-1/resolve", "boris");

        Assert.Equal(HttpStatusCode.OK, fromOpen.StatusCode);
        var resolved = await fromAcked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("resolved", resolved.GetProperty("status").GetString());
        Assert.Equal("boris", resolved.GetProperty("resolved_by").GetString());
        Assert.Equal("anna", resolved.GetProperty("acked_by").GetString());
        Assert.Equal(HttpStatusCode.Conflict, fromResolved.StatusCode);
        Assert.Equal(2, (await QueuedPayloads(factory)).Count);
    }

    [Fact]
    public async Task Ack_OfAResolvedAlert_IsRefused()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await Apply(factory, ActivityRecords.AlertChange("c-1", "resolved", status: "resolved"));

        var response = await Post(client, "/api/alerts/alert-1/ack", "anna");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("conflict", problem.GetProperty("error").GetString());
        Assert.Empty(await QueuedPayloads(factory));
    }

    [Fact]
    public async Task Ack_WithoutAKnownUserOrAnAlert_IsRejectedAndQueuesNothing()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await Apply(factory, ActivityRecords.AlertChange("c-1", "opened"));

        var noUser = await Post(client, "/api/alerts/alert-1/ack", user: null);
        var unknownUser = await Post(client, "/api/alerts/alert-1/ack", "nobody");
        var unknownAlert = await Post(client, "/api/alerts/nope/ack", "anna");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, noUser.StatusCode);
        var problem = await noUser.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_user", problem.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NotFound, unknownUser.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownAlert.StatusCode);
        Assert.Empty(await QueuedPayloads(factory));
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, string? user)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);

        if (user is not null)
        {
            request.Headers.Add("X-User", user);
        }

        return await client.SendAsync(request);
    }

    private static async Task Apply(WebApplicationFactory<Program> factory, string record)
    {
        await using var scope = factory.Services.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IActivityRecordHandler>().HandleAsync(Topics.AlertsChanges, record);
    }

    // Only alerts.changes rows: the seed also leaves config.changes rows in the outbox.
    private static async Task<List<string>> QueuedPayloads(WebApplicationFactory<Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<ApiDbContext>().Outbox
            .Where(message => message.Topic == Topics.AlertsChanges)
            .OrderBy(message => message.Id)
            .Select(message => message.Payload)
            .ToListAsync();
    }
}
