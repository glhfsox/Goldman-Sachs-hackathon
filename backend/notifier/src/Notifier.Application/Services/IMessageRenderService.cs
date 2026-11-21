using Notifier.Application.DTOs.Channels;
using Notifier.Domain.Models;

namespace Notifier.Application.Services;

public interface IMessageRenderService
{
    ChannelMessage Render(Notification notification, int foldedCount);
}
