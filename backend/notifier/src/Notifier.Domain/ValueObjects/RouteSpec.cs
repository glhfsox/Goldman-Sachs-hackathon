namespace Notifier.Domain.ValueObjects;

public sealed record RouteSpec
{
    public RouteSpec(IReadOnlyList<RecipientSpec> recipients, IReadOnlyList<string>? channels)
    {
        ArgumentNullException.ThrowIfNull(recipients);

        Recipients = recipients;
        Channels = channels is { Count: > 0 } ? channels : null;
    }

    public IReadOnlyList<RecipientSpec> Recipients { get; }

    // Null when the rule leaves the choice to each person's preferences.
    public IReadOnlyList<string>? Channels { get; }
}
