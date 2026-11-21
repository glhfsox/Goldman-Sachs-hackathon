using Notifier.Application.Constants;
using Notifier.Application.DTOs.Messages;
using Notifier.Application.Services;
using Notifier.Infrastructure.Messaging;

namespace Notifier.Worker.Workers;

public sealed class AlertChangesWorker : KafkaConsumerWorker<AlertChangeMessage>
{
    public AlertChangesWorker(
        IServiceScopeFactory scopeFactory,
        KafkaConsumerFactory consumerFactory,
        ILogger<AlertChangesWorker> logger)
        : base(scopeFactory, consumerFactory, logger)
    {
    }

    protected override string Topic => Topics.AlertsChanges;

    // One transaction per batch instead of per record: in a burst, planning was bound by per-record round trips and commits.
    protected override int MaxBatchSize => 100;

    protected override async Task HandleAsync(IServiceProvider services, AlertChangeMessage message, CancellationToken cancellationToken)
    {
        var alertChanges = services.GetRequiredService<IAlertChangeService>();
        await alertChanges.HandleAsync(message, cancellationToken);
    }

    protected override async Task HandleBatchAsync(
        IServiceProvider services,
        IReadOnlyList<AlertChangeMessage> messages,
        CancellationToken cancellationToken)
    {
        var alertChanges = services.GetRequiredService<IAlertChangeService>();
        await alertChanges.HandleBatchAsync(messages, cancellationToken);
    }
}
