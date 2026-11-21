using StackExchange.Redis;

namespace AlertApi.Infrastructure.Redis;

public sealed class RedisConnection : IAsyncDisposable
{
    public const string UiChannel = "ui.alerts";

    private readonly Lazy<Task<ConnectionMultiplexer>> _connection;

    public RedisConnection(RedisOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _connection = new Lazy<Task<ConnectionMultiplexer>>(() => ConnectionMultiplexer.ConnectAsync(options.ToConfiguration()));
    }

    public async Task<ISubscriber> GetSubscriberAsync()
    {
        var connection = await _connection.Value;

        return connection.GetSubscriber();
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection.IsValueCreated)
        {
            var connection = await _connection.Value;
            await connection.DisposeAsync();
        }
    }
}
