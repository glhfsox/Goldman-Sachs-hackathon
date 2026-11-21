using Npgsql;

namespace Notifier.Infrastructure.Data;

// contracts/services.md passes databases as postgres:// URLs; Npgsql wants a key=value connection string.
public static class PostgresUrl
{
    public static string ToConnectionString(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        if (!url.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var uri = new Uri(url);
        var credentials = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port <= 0 ? 5432 : uri.Port,
            Database = uri.AbsolutePath.Trim('/'),
            Username = Uri.UnescapeDataString(credentials[0])
        };
        if (credentials.Length > 1)
        {
            builder.Password = Uri.UnescapeDataString(credentials[1]);
        }

        return builder.ConnectionString;
    }
}
