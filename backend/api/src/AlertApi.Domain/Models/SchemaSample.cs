namespace AlertApi.Domain.Models;

// A schema the processor could not map. It waits here until a mapping is saved for it or the ai-worker gives up.
public class SchemaSample
{
    public const string Pending = "pending";
    public const string Mapped = "mapped";
    public const string Failed = "failed";

    private SchemaSample()
    {
    }

    public string Fingerprint { get; private set; } = null!;

    public string SourceId { get; private set; } = null!;

    public string Sample { get; private set; } = null!;

    public string Status { get; private set; } = null!;

    public string? Error { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static SchemaSample Create(string fingerprint, string sourceId, string sample, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sample);

        return new SchemaSample
        {
            Fingerprint = fingerprint,
            SourceId = sourceId,
            Sample = sample,
            Status = Pending,
            CreatedAt = createdAt
        };
    }

    public void MarkMapped()
    {
        Status = Mapped;
        Error = null;
    }

    public void MarkFailed(string? error)
    {
        Status = Failed;
        Error = error;
    }
}
