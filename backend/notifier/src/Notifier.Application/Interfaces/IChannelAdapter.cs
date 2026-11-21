using Notifier.Application.DTOs.Channels;

namespace Notifier.Application.Interfaces;

// Extension point (contracts/runbooks.md §3): a new channel or a real provider is one implementation
// plus one entry in Channels:Enabled. SendAsync must be idempotent on message.NotificationId.
public interface IChannelAdapter
{
    string Channel { get; }

    Task<SendResult> SendAsync(ChannelRecipient recipient, ChannelMessage message, CancellationToken cancellationToken = default);
}
