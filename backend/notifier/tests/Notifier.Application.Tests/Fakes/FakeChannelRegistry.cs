using Notifier.Application.Interfaces;

namespace Notifier.Application.Tests.Fakes;

public sealed class FakeChannelRegistry : IChannelAdapterRegistry
{
    public Dictionary<string, IChannelAdapter> Adapters { get; } = [];

    public IChannelAdapter? Find(string channel) => Adapters.GetValueOrDefault(channel);
}
