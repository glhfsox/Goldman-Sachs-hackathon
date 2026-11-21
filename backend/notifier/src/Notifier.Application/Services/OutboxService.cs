using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Notifier.Application.Interfaces;
using Notifier.Application.Options;
using Notifier.Domain.Repositories;

namespace Notifier.Application.Services;

// The relay of contracts/storage.md: records written in the same transaction as the state are published afterwards.
// A claimed batch is published in one go and its outcome saved in one go.
public sealed class OutboxService : IOutboxService
{
    private readonly IOutboxRepository _outbox;
    private readonly IEventPublisher _publisher;
    private readonly IClock _clock;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxService> _logger;

    public OutboxService(
        IOutboxRepository outbox,
        IEventPublisher publisher,
        IClock clock,
        IOptions<OutboxOptions> options,
        ILogger<OutboxService> logger)
    {
        _outbox = outbox;
        _publisher = publisher;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<int> DispatchAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var batch = (await _outbox.ClaimPendingAsync(
                now,
                _options.BatchSize,
                now.AddSeconds(_options.LeaseSeconds),
                cancellationToken))
            .OrderBy(message => message.Sequence)
            .ToList();
        if (batch.Count == 0)
        {
            return 0;
        }

        var errors = await _publisher.PublishBatchAsync(
            batch.Select(message => new OutgoingEvent(message.Topic, message.Key, message.Payload)).ToList(),
            cancellationToken);

        var doneAt = _clock.UtcNow;
        for (var index = 0; index < batch.Count; index++)
        {
            var message = batch[index];
            if (errors[index] is not { } error)
            {
                message.MarkProcessed(doneAt);
                continue;
            }

            // Left unprocessed, the message is picked up again when its lease runs out.
            _logger.LogWarning("Publishing outbox message {MessageId} to {Topic} failed: {Error}", message.Id, message.Topic, error);
            message.MarkFailed(error, _options.MaxAttempts, doneAt);
        }

        await _outbox.SaveChangesAsync(cancellationToken);

        return batch.Count;
    }
}
