using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using AlertApi.Infrastructure.Data;

namespace AlertApi.Tests.Infrastructure;

[Trait("Category", "Integration")]
public sealed class SourceMigrationTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public SourceMigrationTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task Migrate_OnEmptyDatabase_QueuesOneConfigRecordPerSeededSource()
    {
        var connection = await CreateDatabaseAsync();
        await using var context = new ApiDbContext(new DbContextOptionsBuilder<ApiDbContext>().UseNpgsql(connection).Options);

        await context.Database.MigrateAsync();
        var messages = await context.Outbox
            .Where(message => message.Key.StartsWith("source:"))
            .OrderBy(message => message.Id)
            .ToListAsync();

        Assert.Equal(
            ["source:alertmanager-http", "source:pricing-logs-http", "source:trades-kafka", "source:workflows-kafka"],
            messages.Select(message => message.Key));
        Assert.All(messages, message => Assert.Equal("config.changes", message.Topic));
        var workflows = JsonDocument.Parse(messages.Single(message => message.Key == "source:workflows-kafka").Payload).RootElement;
        Assert.Equal("source", workflows.GetProperty("kind").GetString());
        Assert.False(workflows.GetProperty("deleted").GetBoolean());
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$", workflows.GetProperty("updated_at").GetString());
        var body = workflows.GetProperty("body");
        Assert.Equal("workflows-kafka", body.GetProperty("id").GetString());
        Assert.Equal("kafka", body.GetProperty("kind").GetString());
        Assert.Equal("source.workflows", body.GetProperty("config").GetProperty("topic").GetString());
        Assert.True(body.GetProperty("enabled").GetBoolean());
    }

    private async Task<string> CreateDatabaseAsync()
    {
        var database = $"migration_{Guid.NewGuid():N}";
        await using var admin = new Npgsql.NpgsqlConnection(_postgres.ConnectionString);
        await admin.OpenAsync();
        await using var command = admin.CreateCommand();
        command.CommandText = $"CREATE DATABASE {database}";
        await command.ExecuteNonQueryAsync();

        return new Npgsql.NpgsqlConnectionStringBuilder(_postgres.ConnectionString) { Database = database }.ConnectionString;
    }
}
