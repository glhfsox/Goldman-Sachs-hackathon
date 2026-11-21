namespace Notifier.Application.Options;

public sealed class DeliveryOptions
{
    public const string SectionName = "Delivery";

    public int BatchSize { get; init; } = 50;

    public int MaxAttempts { get; init; } = 5;

    public int BaseBackoffSeconds { get; init; } = 2;

    public int MaxBackoffSeconds { get; init; } = 60;

    public int LeaseSeconds { get; init; } = 30;

    public int PollIntervalMilliseconds { get; init; } = 500;

    // Notifications of one batch sent at the same time, each with its own database connection.
    public int MaxParallelism { get; init; } = 16;

    public string PublicBaseUrl { get; init; } = "http://localhost:3000";

    public Dictionary<string, string> Fallbacks { get; init; } = new()
    {
        ["call"] = "sms",
        ["sms"] = "email",
        ["email"] = "ui"
    };
}
