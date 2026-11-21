using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Notifier.Infrastructure.Data;

// Lets `dotnet ef` build the model without starting the worker (which needs Kafka settings).
public sealed class NotifierDbContextFactory : IDesignTimeDbContextFactory<NotifierDbContext>
{
    private const string LocalDatabaseUrl = "postgres://alerts:alerts@localhost:5435/notifier";

    public NotifierDbContext CreateDbContext(string[] args)
    {
        var url = Environment.GetEnvironmentVariable("DATABASE_URL") ?? LocalDatabaseUrl;
        var options = new DbContextOptionsBuilder<NotifierDbContext>()
            .UseNpgsql(PostgresUrl.ToConnectionString(url))
            .Options;

        return new NotifierDbContext(options);
    }
}
