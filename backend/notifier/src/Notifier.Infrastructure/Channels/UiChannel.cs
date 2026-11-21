using Notifier.Application.DTOs.Channels;
using Notifier.Application.Interfaces;

namespace Notifier.Infrastructure.Channels;

// The card reaches the feed through the processor and the api; a UI "delivery" is the notification_sent
// timeline record the api shows on that card, so there is nothing to call here.
public sealed class UiChannel : IChannelAdapter
{
    public string Channel => "ui";

    public async Task<SendResult> SendAsync(ChannelRecipient recipient, ChannelMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(message);

        await Task.CompletedTask;

        return SendResult.Sent(null);
    }
}
