using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

using Microsoft.Extensions.DependencyInjection;

using AlertApi.Application.Interfaces;

namespace AlertApi.Infrastructure.Realtime;

public sealed class LiveFeed : ILiveFeed
{
    private const int QueueSize = 256;
    private const int MaxKnownAlerts = 10_000;

    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    // alert id -> team id, learned from alert messages, so count and timeline messages can be filtered without the database.
    private readonly ConcurrentDictionary<string, string> _teamByAlert = new();
    private readonly IServiceScopeFactory _scopeFactory;

    public LiveFeed(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task RunAsync(WebSocket socket, string? teamId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(socket);

        var client = new Client(string.IsNullOrWhiteSpace(teamId) ? null : teamId);
        _clients[client.Id] = client;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            var sending = SendLoopAsync(socket, client, linked.Token);
            var receiving = ReceiveLoopAsync(socket, linked.Token);

            await Task.WhenAny(sending, receiving);
            await linked.CancelAsync();
            await Task.WhenAll(Settle(sending), Settle(receiving));
        }
        finally
        {
            _clients.TryRemove(client.Id, out _);
        }
    }

    public async Task BroadcastAsync(string message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (_clients.IsEmpty)
        {
            return;
        }

        using var document = JsonDocument.Parse(message);
        var team = await FindTeamAsync(document.RootElement, cancellationToken);

        foreach (var client in _clients.Values)
        {
            // A client limited to a team skips what it cannot place in that team: the UI refetches on reconnect.
            if (client.TeamId is null || client.TeamId == team)
            {
                client.Queue.Writer.TryWrite(message);
            }
        }
    }

    private async Task<string?> FindTeamAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (message.TryGetProperty("alert", out var alert) && alert.TryGetProperty("team_id", out var teamId))
        {
            var team = teamId.GetString();

            if (team is not null && alert.TryGetProperty("id", out var id) && id.GetString() is { } alertId)
            {
                Remember(alertId, team);
            }

            return team;
        }

        if (!message.TryGetProperty("alert_id", out var alertIdElement) || alertIdElement.GetString() is not { } knownId)
        {
            return null;
        }

        if (_teamByAlert.TryGetValue(knownId, out var cached))
        {
            return cached;
        }

        // Only needed when a browser filters by team.
        if (_clients.Values.All(client => client.TeamId is null))
        {
            return null;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IAlertTeamLookup>().FindTeamAsync(knownId, cancellationToken);

        if (stored is not null)
        {
            Remember(knownId, stored);
        }

        return stored;
    }

    private void Remember(string alertId, string teamId)
    {
        if (_teamByAlert.Count >= MaxKnownAlerts)
        {
            _teamByAlert.Clear();
        }

        _teamByAlert[alertId] = teamId;
    }

    private static async Task SendLoopAsync(WebSocket socket, Client client, CancellationToken cancellationToken)
    {
        await foreach (var message in client.Queue.Reader.ReadAllAsync(cancellationToken))
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
    }

    // The feed is server to client only; reading is how a close from the browser is noticed.
    private static async Task ReceiveLoopAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[256];

        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }
        }
    }

    private static async Task Settle(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
        {
        }
    }

    private sealed class Client
    {
        public Client(string? teamId)
        {
            TeamId = teamId;
            // A slow browser loses its oldest messages instead of holding memory; it refetches anyway.
            Queue = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueSize)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });
        }

        public Guid Id { get; } = Guid.NewGuid();

        public string? TeamId { get; }

        public Channel<string> Queue { get; }
    }
}
