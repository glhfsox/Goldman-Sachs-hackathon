using StackExchange.Redis;

namespace AlertApi.Infrastructure.Redis;

public sealed class RedisOptions
{
    public required string Url { get; init; }

    // redis://host:port/db, the REDIS_URL format of contracts/services.md.
    public ConfigurationOptions ToConfiguration()
    {
        var uri = new Uri(Url);
        var configuration = new ConfigurationOptions
        {
            // The api must start and keep serving REST while Redis is down: live updates are best effort.
            AbortOnConnectFail = false,
            ConnectTimeout = 3000
        };
        configuration.EndPoints.Add(uri.Host, uri.Port > 0 ? uri.Port : 6379);

        if (int.TryParse(uri.AbsolutePath.Trim('/'), out var database))
        {
            configuration.DefaultDatabase = database;
        }

        return configuration;
    }
}
