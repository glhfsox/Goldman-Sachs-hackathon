using Notifier.Application.DTOs.Messages;

namespace Notifier.Application.Services;

public interface IConfigChangeService
{
    Task ApplyAsync(ConfigChangeMessage message, CancellationToken cancellationToken = default);
}
