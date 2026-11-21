namespace Notifier.Application.Options;

public sealed class ConfigCacheOptions
{
    public const string SectionName = "ConfigCache";

    // How stale another replica's copy of the configuration may get.
    public double CacheSeconds { get; init; } = 5;
}
