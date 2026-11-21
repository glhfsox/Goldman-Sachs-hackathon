namespace Notifier.Application.Options;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    public int BatchSize { get; init; } = 100;

    public int MaxAttempts { get; init; } = 10;

    public int LeaseSeconds { get; init; } = 30;

    public int PollIntervalMilliseconds { get; init; } = 200;
}
