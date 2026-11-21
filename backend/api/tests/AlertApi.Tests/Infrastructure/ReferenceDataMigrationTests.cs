using Microsoft.EntityFrameworkCore;

using AlertApi.Infrastructure.Data;

namespace AlertApi.Tests.Infrastructure;

[Trait("Category", "Integration")]
public sealed class ReferenceDataMigrationTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public ReferenceDataMigrationTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task Migrate_OnEmptyDatabase_QueuesOneRecordPerSeededRow()
    {
        var connection = await CreateDatabaseAsync();
        await using var context = new ApiDbContext(new DbContextOptionsBuilder<ApiDbContext>().UseNpgsql(connection).Options);

        await context.Database.MigrateAsync();
        var keys = await context.Outbox.Select(message => message.Key).ToListAsync();

        Assert.Equal(4, keys.Count(key => key.StartsWith("team:")));
        Assert.Equal(5, keys.Count(key => key.StartsWith("user:")));
        Assert.Equal(5, keys.Count(key => key.StartsWith("membership:")));
        Assert.Equal(2, keys.Count(key => key.StartsWith("service_dep:")));
        Assert.Equal(2, keys.Count(key => key.StartsWith("preferences:")));
        Assert.Contains("service_dep:payments-api:counterparty-service", keys);
        Assert.Contains("membership:settlements:boris", keys);
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
