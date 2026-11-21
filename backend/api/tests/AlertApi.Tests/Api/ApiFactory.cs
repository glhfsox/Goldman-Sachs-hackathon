using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using AlertApi.Infrastructure.Kafka;

using AlertApi.Tests.Infrastructure;

namespace AlertApi.Tests.Api;

public static class ApiFactory
{
    // Every factory gets its own database, so tests never see each other's rows.
    public static WebApplicationFactory<Program> Create(PostgresFixture postgres, string environment, bool enableSwagger)
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
            host.UseSetting("ENABLE_SWAGGER", enableSwagger ? "true" : "false");
            // Tests feed records to the handler directly; no consumer should poll a broker that does not exist.
            host.ConfigureServices(services =>
            {
                var consumer = services.Single(descriptor => descriptor.ImplementationType == typeof(ActivityConsumer));
                services.Remove(consumer);
            });
        });
    }
}
