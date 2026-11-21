using Npgsql;

namespace AlertApi.Infrastructure.Data;

public static class DatabaseUrl
{
    // DATABASE_URL has the form postgres://user:password@host:port/database (contracts/services.md).
    public static string ToConnectionString(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var uri = new Uri(url);
        var credentials = uri.UserInfo.Split(':', 2);

        return new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = uri.AbsolutePath.TrimStart('/'),
            Username = Uri.UnescapeDataString(credentials[0]),
            Password = credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : null
        }.ConnectionString;
    }
}
