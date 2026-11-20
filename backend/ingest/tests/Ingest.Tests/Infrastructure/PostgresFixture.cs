using Testcontainers.PostgreSql;

namespace Ingest.Tests.Infrastructure;

public sealed class PostgresFixture : Xunit.IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:17").Build();

    public string ConnectionString => _container.GetConnectionString();

    public string DatabaseUrl
    {
        get
        {
            var builder = new Npgsql.NpgsqlConnectionStringBuilder(ConnectionString);

            return $"postgres://{builder.Username}:{builder.Password}@{builder.Host}:{builder.Port}/{builder.Database}";
        }
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
