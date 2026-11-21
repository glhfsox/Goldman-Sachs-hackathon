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

        EnsureConfigFitsKind(kind, config);

        return new Source
        {
            Id = id,
            Kind = kind,
            Config = config,
            Enabled = enabled,
            UpdatedAt = updatedAt
        };
    }

    // False when the change is not newer than the row: a replayed or reordered record must not win.
    public bool ApplyChange(
        SourceKind kind,
        SourceConfig config,
        bool enabled,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (updatedAt <= UpdatedAt)
        {
            return false;
        }

        EnsureConfigFitsKind(kind, config);

        Kind = kind;
        Config = config;
        Enabled = enabled;
        UpdatedAt = updatedAt;

        return true;
    }

    // A kafka source without a topic could never be consumed.
    private static void EnsureConfigFitsKind(SourceKind kind, SourceConfig config)
    {
        if (kind == SourceKind.Kafka)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(config.Topic);
        }
    }
}
