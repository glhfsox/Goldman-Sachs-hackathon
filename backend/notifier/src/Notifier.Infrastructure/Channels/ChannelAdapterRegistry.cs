using Microsoft.Extensions.Options;

using Notifier.Application.Interfaces;

namespace Notifier.Infrastructure.Channels;

public sealed class ChannelAdapterRegistry : IChannelAdapterRegistry
{
    private readonly Dictionary<string, IChannelAdapter> _adapters;

    public ChannelAdapterRegistry(
        IEnumerable<IChannelAdapter> adapters,
        IOptions<ChannelOptions> options)
    {
        var enabled = options.Value.Enabled
            .Select(name => name.Trim().ToLowerInvariant())
            .ToHashSet();

        _adapters = adapters
            .Where(adapter => enabled.Contains(adapter.Channel))
            .ToDictionary(adapter => adapter.Channel);
    }

    public IChannelAdapter? Find(string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);

        return _adapters.GetValueOrDefault(channel.ToLowerInvariant());
    }
}
