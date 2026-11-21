using AlertApi.Application.DTOs.Responses;

namespace AlertApi.Application.Interfaces;

// Messages for browsers on Redis ui.alerts (contracts/events.md section 5). Loss is acceptable: the UI refetches,
// so a failure here must never fail the work that caused the message.
public interface IUiPublisher
{
    Task PublishTimelineAsync(string alertId, CancellationToken cancellationToken = default);

    Task PublishAlertAsync(string change, AlertResponse alert, CancellationToken cancellationToken = default);
}
