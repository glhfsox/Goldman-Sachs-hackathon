namespace Notifier.Domain.ValueObjects;

public sealed record Severity
{
    private static readonly string[] Scale = ["info", "warning", "error", "critical"];

    public static readonly Severity Info = new("info");
    public static readonly Severity Warning = new("warning");
    public static readonly Severity Error = new("error");
    public static readonly Severity Critical = new("critical");

    public Severity(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var normalized = name.Trim().ToLowerInvariant();
        if (Array.IndexOf(Scale, normalized) < 0)
        {
            throw new ArgumentException($"Severity '{name}' is not one of info, warning, error, critical.", nameof(name));
        }

        Name = normalized;
    }

    public string Name { get; }

    public int Rank => Array.IndexOf(Scale, Name);

    // Producers normalize severity, but a bad value must not stop delivery; warning is the unmapped default (events.md).
    public static Severity ParseOrDefault(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || Array.IndexOf(Scale, name.Trim().ToLowerInvariant()) < 0)
        {
            return Warning;
        }

        return new Severity(name);
    }

    public bool IsAtLeast(Severity other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return Rank >= other.Rank;
    }

    public override string ToString() => Name;
}
