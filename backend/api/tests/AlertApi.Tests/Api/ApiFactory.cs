using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using AlertApi.Application.Interfaces;
using AlertApi.Infrastructure.Redis;

using AlertApi.Infrastructure.Kafka;

using AlertApi.Tests.Infrastructure;

namespace AlertApi.Tests.Api;

public static class ApiFactory
{
    // Every factory gets its own database, so tests never see each other's rows.
    // Without a redisUrl the live updates go to a recording fake and nothing subscribes to Redis.
    public static WebApplicationFactory<Program> Create(
        PostgresFixture postgres,
        string environment,
        bool enableSwagger,
        string? redisUrl = null,
        bool runExecutor = false)
    {
        var database = $"api_{Guid.NewGuid():N}";
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(postgres.ConnectionString);

        using (var admin = new Npgsql.NpgsqlConnection(postgres.ConnectionString))
        {
            admin.Open();
            using var command = admin.CreateCommand();
            command.CommandText = $"CREATE DATABASE {database}";
            command.ExecuteNonQuery();
        }

        return new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment(environment);
            host.UseSetting("DATABASE_URL", $"postgres://{builder.Username}:{builder.Password}@{builder.Host}:{builder.Port}/{database}");
            // The producer connects lazily; queued outbox rows stay in the table and the publisher logs and retries.
            host.UseSetting("KAFKA_BROKERS", "localhost:1");
            host.UseSetting("REDIS_URL", redisUrl ?? "redis://localhost:1/0");
            // Tests that look at a run's first state must not race a worker; the executor tests switch it on.
            host.UseSetting("RUNBOOK_EXECUTOR", runExecutor ? "true" : "false");
            host.UseSetting("RUNBOOK_POLL_MS", "50");
            host.UseSetting("ENABLE_SWAGGER", enableSwagger ? "true" : "false");
            // Tests feed records to the handler directly; no consumer should poll a broker that does not exist.
            host.ConfigureServices(services =>
            {
                var consumer = services.Single(descriptor => descriptor.ImplementationType == typeof(ActivityConsumer));
                services.Remove(consumer);

                if (redisUrl is null)
                {
                    services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(UiAlertsSubscriber)));
                    services.RemoveAll<IUiPublisher>();
                    services.AddSingleton<RecordingUiPublisher>();
                    services.AddSingleton<IUiPublisher>(provider => provider.GetRequiredService<RecordingUiPublisher>());
                }
            });
        });
    }
}
