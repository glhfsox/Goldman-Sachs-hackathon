using Notifier.Application.DTOs.Messages;

namespace Notifier.Application.Services;

public interface IAlertChangeService
{
    Task HandleAsync(AlertChangeMessage message, CancellationToken cancellationToken = default);
}
