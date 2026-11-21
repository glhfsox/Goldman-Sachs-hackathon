namespace Ingest.Domain.ValueObjects;

// Brokers is optional: a null value means the default sources cluster (SOURCE_KAFKA_BROKERS).
public sealed record SourceConfig(string? Topic, string? Brokers)
{
    public static SourceConfig Empty { get; } = new(null, null);
}
