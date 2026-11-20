namespace Notifier.Domain.ValueObjects;

public sealed record PlannedDelivery(
    Channel Channel,
    DateTime SendAt,
    int Step,
    string Reason);
