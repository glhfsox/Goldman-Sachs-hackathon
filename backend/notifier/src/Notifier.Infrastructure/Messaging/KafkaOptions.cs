namespace Notifier.Infrastructure.Messaging;

public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    public string Brokers { get; init; } = string.Empty;

    public string GroupId { get; init; } = "notifier";
}
