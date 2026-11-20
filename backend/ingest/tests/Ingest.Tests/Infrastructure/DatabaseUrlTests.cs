using Ingest.Infrastructure.Data;

namespace Ingest.Tests.Infrastructure;

public sealed class DatabaseUrlTests
{
    [Fact]
    public void ToConnectionString_ParsesContractFormat()
    {
        var result = DatabaseUrl.ToConnectionString("postgres://alerts:p%40ss@ingest-db:5432/ingest");

        Assert.Contains("Host=ingest-db", result);
        Assert.Contains("Database=ingest", result);
        Assert.Contains("Username=alerts", result);
        Assert.Contains("Password=p@ss", result);
    }
}
