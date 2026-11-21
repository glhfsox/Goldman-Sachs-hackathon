using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Testcontainers.Redis;

using AlertApi.Application.Constants;
using AlertApi.Application.DTOs.Messages;
using AlertApi.Application.Interfaces;
using AlertApi.Tests.Infrastructure;

namespace AlertApi.Tests.Api;

[Trait("Category", "Integration")]
public sealed class LiveUpdateTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private readonly RedisContainer _redis = new RedisBuilder().WithImage("redis:7.4.11").Build();

    public LiveUpdateTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    public async Task InitializeAsync()
    {
        await _redis.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _redis.DisposeAsync();
    }

    [Fact]
    public async Task Timeline_AndPersonChanges_ReachABrowserThroughRedis_AndCountUpdatesTheCopy()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false, RedisUrl());
        using var client = factory.CreateClient();
        using var socket = await LiveFeedTests.Connect(factory, "?team=settlements");

        await Handle(factory, Topics.AlertsChanges, ActivityRecords.AlertChange("c-1", "opened"));
        var opened = await LiveFeedTests.Receive(socket, 1);
        await Handle(factory, Topics.AlertsChanges, ActivityRecords.AlertChange("c-2", "acked", status: "acked", ackedBy: "anna").Replace("\"processor\"", "\"anna\""));
        var acked = await LiveFeedTests.Receive(socket, 2);
        await Publish(factory, """{"type": "count", "alert_id": "alert-1", "count": 500, "last_seen": "2026-11-20T10:16:01.004Z"}""");
        var count = await LiveFeedTests.Receive(socket, 1);
        await Task.Delay(500);
        var detail = await client.GetFromJsonAsync<JsonElement>("/api/alerts/alert-1");

        Assert.Equal("""{"type":"timeline","alert_id":"alert-1"}""", Assert.Single(opened));
        Assert.Equal(["timeline", "alert"], acked.Select(Type));
        Assert.Equal("acked", JsonDocument.Parse(acked[1]).RootElement.GetProperty("alert").GetProperty("status").GetString());
        Assert.Equal("count", Type(Assert.Single(count)));
        Assert.Equal(500, detail.GetProperty("alert").GetProperty("count").GetInt64());
        Assert.Equal("2026-11-20T10:16:01.004Z", detail.GetProperty("alert").GetProperty("last_seen").GetString());
    }

    private string RedisUrl()
    {
        var connection = new Uri("redis://" + _redis.GetConnectionString().Split(',')[0]);

        return $"redis://{connection.Host}:{connection.Port}/0";
    }

    private static string Type(string message)
    {
        return JsonDocument.Parse(message).RootElement.GetProperty("type").GetString()!;
    }

    private static async Task Handle(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, string topic, string value)
    {
        await using var scope = factory.Services.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IActivityRecordHandler>().HandleAsync(topic, value);
    }

    private static async Task Publish(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, string message)
    {
        var redis = factory.Services.GetRequiredService<AlertApi.Infrastructure.Redis.RedisConnection>();
        var subscriber = await redis.GetSubscriberAsync();

        await subscriber.PublishAsync(StackExchange.Redis.RedisChannel.Literal("ui.alerts"), message);
    }
}
