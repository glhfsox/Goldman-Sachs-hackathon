using System.Text.Json;

using Microsoft.Extensions.Logging;

using StackExchange.Redis;

using AlertApi.Application.DTOs.Responses;
using AlertApi.Application.Interfaces;
using AlertApi.Application.Serialization;

namespace AlertApi.Infrastructure.Redis;

public sealed class RedisUiPublisher : IUiPublisher
{
    private readonly RedisConnection _connection;
    private readonly ILogger<RedisUiPublisher> _logger;

    public RedisUiPublisher(RedisConnection connection, ILogger<RedisUiPublisher> logger)
    {
        _connection = connection;
        _logger = logger;
    }

    public async Task PublishTimelineAsync(string alertId, CancellationToken cancellationToken = default)
    {
        await PublishAsync(JsonSerializer.Serialize(new { Type = "timeline", AlertId = alertId }, RecordJson.Options));
    }

    public async Task PublishAlertAsync(string change, AlertResponse alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

        await PublishAsync(JsonSerializer.Serialize(new { Type = "alert", Change = change, Alert = alert }, RecordJson.Options));
    }

    private async Task PublishAsync(string message)
    {
        try
        {
            var subscriber = await _connection.GetSubscriberAsync();

            // Fire and forget: a message lost while Redis is down is repaired when the UI refetches.
            await subscriber.PublishAsync(RedisChannel.Literal(RedisConnection.UiChannel), message, CommandFlags.FireAndForget);
        }
        catch (RedisException exception)
        {
            _logger.LogWarning(exception, "Publishing to Redis {Channel} failed; the UI refetches.", RedisConnection.UiChannel);
        }
    }
}
