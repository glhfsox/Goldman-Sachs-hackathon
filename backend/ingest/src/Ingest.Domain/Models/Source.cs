using Ingest.Domain.ValueObjects;

namespace Ingest.Domain.Models;

public class Source
{
    private Source()
    {
    }

    public string Id { get; private set; } = null!;

    public SourceKind Kind { get; private set; }

    public SourceConfig Config { get; private set; } = null!;

    public bool Enabled { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Source Create(
        string id,
        SourceKind kind,
        SourceConfig config,
        bool enabled,
        DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(config);

        // A kafka source without a topic could never be consumed.
        if (kind == SourceKind.Kafka)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(config.Topic);
        }

        return new Source
        {
            Id = id,
            Kind = kind,
            Config = config,
            Enabled = enabled,
            UpdatedAt = updatedAt
        };
    }
}
