using Notifier.Domain.Models;

namespace Notifier.Application.Services;

public interface ITimelineService
{
    Task AppendAsync(Notification notification, string kind, DateTime now, CancellationToken cancellationToken = default);
}
