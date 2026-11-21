namespace Notifier.Infrastructure.Channels;

public sealed record GatewayRequest(
    string NotificationId,
    string To,
    string Text);
