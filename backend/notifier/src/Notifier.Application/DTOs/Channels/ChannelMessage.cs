namespace Notifier.Application.DTOs.Channels;

// Channel adapter argument "message" (contracts/runbooks.md §3), already rendered for its channel.
public sealed record ChannelMessage(
    string NotificationId,
    string Channel,
    string AlertId,
    string Severity,
    string Title,
    string Body,
    string Link);
