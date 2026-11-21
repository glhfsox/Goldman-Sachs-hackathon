namespace Notifier.MockGateway.DTOs.Responses;

public sealed record DeliveryResponse(
    string Id,
    string Channel,
    string NotificationId,
    string To,
    string Text,
    DateTimeOffset ReceivedAt);
