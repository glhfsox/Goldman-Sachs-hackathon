using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Notifier.Application.Interfaces;
using Notifier.Application.Options;
using Notifier.Domain.Repositories;

namespace Notifier.Application.Services;

// The relay of contracts/storage.md: records written in the same transaction as the state are published afterwards.
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
        var batch = await _outbox.ClaimPendingAsync(
            now,
            _options.BatchSize,
            now.AddSeconds(_options.LeaseSeconds),
            cancellationToken);

        foreach (var message in batch.OrderBy(message => message.Sequence))
        {
            try
            {
                await _publisher.PublishAsync(message.Topic, message.Key, message.Payload, cancellationToken);
                message.MarkProcessed(_clock.UtcNow);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Left unprocessed, the message is picked up again when its lease runs out.
                _logger.LogWarning(exception, "Publishing outbox message {MessageId} to {Topic} failed", message.Id, message.Topic);
                message.MarkFailed(exception.Message, _options.MaxAttempts, _clock.UtcNow);
            }

            await _outbox.SaveChangesAsync(cancellationToken);
        }

        return batch.Count;
    }
}
