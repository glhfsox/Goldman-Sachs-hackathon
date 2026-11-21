namespace Notifier.Application.Interfaces;

public interface IChannelAdapterRegistry
{
    IChannelAdapter? Find(string channel);
}
