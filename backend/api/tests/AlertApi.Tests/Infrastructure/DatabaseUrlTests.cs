using AlertApi.Infrastructure.Data;

namespace AlertApi.Tests.Infrastructure;

public sealed class DatabaseUrlTests
{
    [Fact]
    public void ToConnectionString_ParsesContractFormat()
    {
        var result = DatabaseUrl.ToConnectionString("postgres://alerts:p%40ss@api-db:5432/api");

        Assert.Contains("Host=api-db", result);
        Assert.Contains("Database=api", result);
        Assert.Contains("Username=alerts", result);
        Assert.Contains("Password=p@ss", result);
    }
}
