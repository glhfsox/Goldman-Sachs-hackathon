using Notifier.Domain.ValueObjects;

namespace Notifier.Domain.Services;

public sealed class FallbackPolicy
{
    public Channel? Next(Channel failed, IReadOnlyDictionary<string, string> fallbacks)
    {
        ArgumentNullException.ThrowIfNull(failed);
        ArgumentNullException.ThrowIfNull(fallbacks);

        if (!fallbacks.TryGetValue(failed.Name, out var next) || string.IsNullOrWhiteSpace(next))
        {
            return null;
        }

        var channel = new Channel(next);

        return channel == failed ? null : channel;
    }
}
