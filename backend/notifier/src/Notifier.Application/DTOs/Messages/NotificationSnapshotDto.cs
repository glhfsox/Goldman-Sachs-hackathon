namespace Notifier.Application.DTOs.Messages;

// The api keeps this object as its copy of the notification ("why you got this / why not", AC-5.6).
public sealed record NotificationSnapshotDto(
    string Id,
    string UserId,
    string Channel,
    string Status,
    string Reason,
    DateTime SendAt,
    DateTime? SentAt,
    string? Error);
