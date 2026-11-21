using System.Net.WebSockets;
using System.Text;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using AlertApi.Application.Constants;
using AlertApi.Application.DTOs.Messages;
using AlertApi.Application.Interfaces;
using AlertApi.Tests.Infrastructure;

namespace AlertApi.Tests.Api;

[Trait("Category", "Integration")]
public sealed class LiveFeedTests : IClassFixture<PostgresFixture>
{
    private const string SettlementsAlert = """{"type": "alert", "change": "opened", "alert": {"id": "alert-1", "team_id": "settlements"}}""";
    private const string PricingAlert = """{"type": "alert", "change": "opened", "alert": {"id": "alert-2", "team_id": "pricing"}}""";
    private const string TimelineOne = """{"type": "timeline", "alert_id": "alert-1"}""";
    private const string TimelineTwo = """{"type": "timeline", "alert_id": "alert-2"}""";

    private readonly PostgresFixture _postgres;

    public LiveFeedTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task Socket_WithATeam_GetsOnlyThatTeamsMessages_AlsoCountAndTimelineOfKnownAlerts()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var settlements = await Connect(factory, "?team=settlements");
        using var everyone = await Connect(factory, string.Empty);
        var feed = factory.Services.GetRequiredService<ILiveFeed>();

        await feed.BroadcastAsync(SettlementsAlert);
        await feed.BroadcastAsync(PricingAlert);
        await feed.BroadcastAsync(TimelineTwo);
        await feed.BroadcastAsync(TimelineOne);

        Assert.Equal([SettlementsAlert, TimelineOne], await Receive(settlements, 2));
        Assert.Equal([SettlementsAlert, PricingAlert, TimelineTwo, TimelineOne], await Receive(everyone, 4));
    }

    [Fact]
    public async Task Socket_WithATeam_PlacesAnUnseenAlertThroughTheDatabase()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IActivityRecordHandler>()
                .HandleAsync(Topics.AlertsChanges, ActivityRecords.AlertChange("c-1", "opened"));
        }

        using var settlements = await Connect(factory, "?team=settlements");
        using var pricing = await Connect(factory, "?team=pricing");

        await factory.Services.GetRequiredService<ILiveFeed>().BroadcastAsync(TimelineOne);

        Assert.Equal([TimelineOne], await Receive(settlements, 1));
        Assert.Empty(await Receive(pricing, 0));
    }

    [Fact]
    public async Task Connect_WithoutAWebSocketRequest_Returns400()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/ws");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    internal static async Task<WebSocket> Connect(WebApplicationFactory<Program> factory, string query)
    {
        factory.CreateClient().Dispose();
        var client = factory.Server.CreateWebSocketClient();
        var socket = await client.ConnectAsync(new Uri($"ws://localhost/ws{query}"), CancellationToken.None);

        // The socket is registered with the feed a moment after the handshake.
        await Task.Delay(200);

        return socket;
    }

    internal static async Task<List<string>> Receive(WebSocket socket, int expected)
    {
        var messages = new List<string>();
        var buffer = new byte[8192];
        using var timeout = new CancellationTokenSource(expected == 0 ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(10));

        try
        {
            while (messages.Count < expected || expected == 0)
            {
                var result = await socket.ReceiveAsync(buffer, timeout.Token);
                messages.Add(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
        }
        catch (OperationCanceledException)
        {
        }

        return messages;
    }
}
