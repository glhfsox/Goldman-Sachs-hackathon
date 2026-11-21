using AlertApi.Domain.Models;

namespace AlertApi.Application.DTOs.Responses;

public sealed record NotificationResponse(
    string Id,
    string UserId,
    string Channel,
    string Status,
    string Reason,
    DateTimeOffset SendAt,
    DateTimeOffset? SentAt,
    string? Error)
{
    public static NotificationResponse From(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        return new NotificationResponse(
            notification.Id,
            notification.UserId,
            notification.Channel,
            notification.Status,
            notification.Reason,
            notification.SendAt,
            notification.SentAt,
            notification.Error);
    }
}
