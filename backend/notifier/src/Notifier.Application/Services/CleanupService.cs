using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Notifier.Application.Interfaces;
using Notifier.Application.Options;
using Notifier.Domain.Repositories;

namespace Notifier.Application.Services;

// Keeps the working tables small, so claims and dedup lookups stay fast however long the service runs.
public sealed class CleanupService : ICleanupService
{
    private readonly IOutboxRepository _outbox;
    private readonly IProcessedChangeRepository _processedChanges;
    private readonly INotificationRepository _notifications;
    private readonly IClock _clock;
    private readonly CleanupOptions _options;
    private readonly ILogger<CleanupService> _logger;

    public CleanupService(
        IOutboxRepository outbox,
        IProcessedChangeRepository processedChanges,
        INotificationRepository notifications,
        IClock clock,
        IOptions<CleanupOptions> options,
        ILogger<CleanupService> logger)
    {
        _outbox = outbox;
        _processedChanges = processedChanges;
        _notifications = notifications;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<int> CleanUpAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var outbox = await _outbox.DeleteProcessedBeforeAsync(now.AddHours(-_options.OutboxRetentionHours), cancellationToken);
        var changes = await _processedChanges.DeleteBeforeAsync(now.AddHours(-_options.ProcessedChangeRetentionHours), cancellationToken);
        var notifications = await _notifications.DeleteFinishedBeforeAsync(now.AddHours(-_options.NotificationRetentionHours), cancellationToken);

        var deleted = outbox + changes + notifications;
        if (deleted > 0)
        {
            _logger.LogInformation(
                "Cleanup deleted {Outbox} outbox records, {Changes} processed changes and {Notifications} notifications",
                outbox,
                changes,
                notifications);
        }

        return deleted;
    }
}
