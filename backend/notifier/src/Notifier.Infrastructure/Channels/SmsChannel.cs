using Microsoft.Extensions.Options;

using Notifier.Application.DTOs.Channels;
using Notifier.Application.Interfaces;

namespace Notifier.Infrastructure.Channels;

public sealed class SmsChannel : IChannelAdapter
{
    private readonly GatewayClient _gateway;
    private readonly ChannelOptions _options;

    public SmsChannel(
        GatewayClient gateway,
        IOptions<ChannelOptions> options)
    {
        _gateway = gateway;
        _options = options.Value;
    }

    public string Channel => "sms";

    public async Task<SendResult> SendAsync(ChannelRecipient recipient, ChannelMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(message);

        if (string.IsNullOrWhiteSpace(recipient.Phone))
        {
            return SendResult.Failed($"User '{recipient.UserId}' has no phone number.");
        }

        var request = new GatewayRequest(message.NotificationId, recipient.Phone, message.Body);

        return await _gateway.PostAsync(_options.SmsGatewayUrl, request, cancellationToken);
    }
}
