namespace AlertApi.Application.DTOs.Messages;

public sealed record NotificationMessage(
    string Id,
    string UserId,
    string Channel,
    string Status,
    string Reason,
    DateTimeOffset SendAt,
    DateTimeOffset? SentAt,
    string? Error);
