using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using StackExchange.Redis;

using AlertApi.Application.DTOs.Messages;
using AlertApi.Application.Interfaces;
using AlertApi.Application.Serialization;

namespace AlertApi.Infrastructure.Redis;

// Every replica reads ui.alerts: it keeps the alert copy's counters current and pushes each message to its own browsers.
public sealed class UiAlertsSubscriber : BackgroundService
{
    private readonly RedisConnection _connection;
    private readonly ILiveFeed _liveFeed;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UiAlertsSubscriber> _logger;

    public UiAlertsSubscriber(
        RedisConnection connection,
        ILiveFeed liveFeed,
        IServiceScopeFactory scopeFactory,
        ILogger<UiAlertsSubscriber> logger)
    {
        _connection = connection;
        _liveFeed = liveFeed;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var subscriber = await _connection.GetSubscriberAsync();
            var queue = await subscriber.SubscribeAsync(RedisChannel.Literal(RedisConnection.UiChannel));
            queue.OnMessage(message => HandleAsync(message.Message.ToString(), stoppingToken));

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public async Task HandleAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            await ApplyCountAsync(message, cancellationToken);
            await _liveFeed.BroadcastAsync(message, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // One bad message must not stop the subscription.
            _logger.LogWarning(exception, "Handling a ui.alerts message failed; the UI refetches.");
        }
    }

    private async Task ApplyCountAsync(string message, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(message);

        if (document.RootElement.GetProperty("type").GetString() != "count")
        {
            return;
        }

        var count = document.RootElement.Deserialize<CountMessage>(RecordJson.Options)
            ?? throw new JsonException("The count message is empty.");

        await using var scope = _scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IActivityService>().ApplyCountAsync(count, cancellationToken);
    }
}
