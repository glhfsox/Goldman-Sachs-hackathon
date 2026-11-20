using Notifier.Application.DTOs.Channels;
using Notifier.Application.Interfaces;

namespace Notifier.Application.Tests.Fakes;

public sealed class FakeChannel : IChannelAdapter
{
    public FakeChannel(string channel, SendResult result)
    {
        Channel = channel;
        Result = result;
    }

    public string Channel { get; }

    public SendResult Result { get; set; }

    public List<ChannelMessage> Sent { get; } = [];

    public async Task<SendResult> SendAsync(ChannelRecipient recipient, ChannelMessage message, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        Sent.Add(message);

        return Result;
    }
}
