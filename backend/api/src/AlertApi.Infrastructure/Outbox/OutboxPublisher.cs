using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using AlertApi.Application.Interfaces;
using AlertApi.Infrastructure.Data;

namespace AlertApi.Infrastructure.Outbox;

// Posts outbox rows to Kafka and deletes them once Kafka confirmed. A crash between the two repeats a record,
// which is fine: every consumer is idempotent (AGENTS.md "Kafka events").
public sealed class OutboxPublisher : BackgroundService
{
    private const int BatchSize = 100;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _retryDelay;
    private readonly ILogger<OutboxPublisher> _logger;

    public OutboxPublisher(
        IServiceScopeFactory scopeFactory,
        TimeSpan pollInterval,
        TimeSpan retryDelay,
        ILogger<OutboxPublisher> logger)
    {
        _scopeFactory = scopeFactory;
        _pollInterval = pollInterval;
        _retryDelay = retryDelay;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = _pollInterval;

            try
            {
                var published = await PublishBatchAsync(stoppingToken);

                if (published == BatchSize)
                {
                    delay = TimeSpan.Zero;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Publishing outbox records failed; retrying in {Delay}.", _retryDelay);
                delay = _retryDelay;
            }

            await Task.Delay(delay, stoppingToken);
        }
    }

    public async Task<int> PublishBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // SKIP LOCKED lets replicas work on different rows instead of waiting for each other.
        var messages = await context.Outbox
            .FromSqlRaw("SELECT id, topic, key, payload, created_at FROM outbox ORDER BY id LIMIT {0} FOR UPDATE SKIP LOCKED", BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var message in messages)
        {
            await publisher.PublishAsync(message.Topic, message.Key, message.Payload, cancellationToken);
            context.Outbox.Remove(message);
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return messages.Count;
    }
}
