namespace AlertApi.Infrastructure.Kafka;

public sealed class ActivityConsumerOptions
{
    public required string Brokers { get; init; }

    public string GroupId { get; init; } = "api";

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);
}
