using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using AlertApi.Tests.Infrastructure;

namespace AlertApi.Tests.Api;

[Trait("Category", "Integration")]
public sealed class HealthTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public HealthTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task Health_AfterMigrating_ReturnsOk()
    {
        using var factory = CreateFactory("Production", enableSwagger: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", body.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(true, HttpStatusCode.OK)]
    [InlineData(false, HttpStatusCode.NotFound)]
    public async Task OpenApiSpec_IsServedOnlyWithTheFlagInProduction(bool enableSwagger, HttpStatusCode expected)
    {
        using var factory = CreateFactory("Production", enableSwagger);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(expected, response.StatusCode);
    }

    private WebApplicationFactory<Program> CreateFactory(string environment, bool enableSwagger)
    {
        var database = $"health_{Guid.NewGuid():N}";
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(_postgres.ConnectionString);
        builder.Database = database;

        using (var admin = new Npgsql.NpgsqlConnection(_postgres.ConnectionString))
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
            // The producer connects lazily; with no rows in the outbox nothing is sent.
            host.UseSetting("KAFKA_BROKERS", "localhost:1");
            host.UseSetting("ENABLE_SWAGGER", enableSwagger ? "true" : "false");
        });
    }
}
