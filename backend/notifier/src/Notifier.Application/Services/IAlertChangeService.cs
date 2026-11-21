using Notifier.Application.DTOs.Messages;

namespace Notifier.Application.Services;

public interface IAlertChangeService
{
    Task HandleAsync(AlertChangeMessage message, CancellationToken cancellationToken = default);

    // The records in consumption order, applied in one transaction. An invalid record is logged and skipped.
    Task HandleBatchAsync(IReadOnlyList<AlertChangeMessage> messages, CancellationToken cancellationToken = default);
}
