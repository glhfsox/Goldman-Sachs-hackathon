namespace Notifier.Domain.ValueObjects;

// A non-UI notification that counts as "this person was told about this group (incident)".
public sealed record NotifiedEntry(
    string GroupKey,
    string UserId,
    string AlertId,
    DateTime CreatedAt);
