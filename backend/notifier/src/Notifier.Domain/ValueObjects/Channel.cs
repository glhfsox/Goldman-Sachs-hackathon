namespace Notifier.Domain.ValueObjects;

public sealed record Channel
{
    public static readonly Channel Ui = new("ui");
    public static readonly Channel Email = new("email");
    public static readonly Channel Sms = new("sms");
    public static readonly Channel Call = new("call");

    public Channel(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name.Trim().ToLowerInvariant();
    }

    public string Name { get; }

    // Only channels that interrupt a person count against max_push_per_hour (rules.md §5).
    public bool IsPush => Name == "sms" || Name == "call";

    public override string ToString() => Name;
}
