using Confluent.Kafka;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using AlertApi.Application.Constants;
using AlertApi.Application.Exceptions;
using AlertApi.Application.Interfaces;

namespace AlertApi.Infrastructure.Kafka;

// Feeds the api's read copies from alerts.changes, alerts.timeline and schema.samples. All replicas share one
// consumer group, so Kafka splits the partitions between them.
public sealed class ActivityConsumer : BackgroundService
{
    private static readonly string[] TopicNames = [Topics.AlertsChanges, Topics.AlertsTimeline, Topics.SchemaSamples];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ActivityConsumerOptions _options;
    private readonly ILogger<ActivityConsumer> _logger;

    public ActivityConsumer(
        IServiceScopeFactory scopeFactory,
        ActivityConsumerOptions options,
        ILogger<ActivityConsumer> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The Kafka client blocks while it waits for records; yield so host startup does not wait for it.
        await Task.Yield();

        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = _options.Brokers,
            GroupId = _options.GroupId,
            // A new api database starts from the beginning of each topic and rebuilds its copies.
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoOffsetStore = false
        }).Build();

        consumer.Subscribe(TopicNames);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var result = ConsumeOne(consumer);

                if (result is null)
                {
                    continue;
                }

                await HandleWithRetryAsync(result, stoppingToken);
                // The offset is stored only after the record is applied or rejected for good.
                consumer.StoreOffset(result);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            consumer.Close();
        }
    }

    private ConsumeResult<string, string>? ConsumeOne(IConsumer<string, string> consumer)
    {
        try
        {
            return consumer.Consume(TimeSpan.FromMilliseconds(500));
        }
        catch (ConsumeException exception)
        {
            _logger.LogWarning(exception, "Reading from Kafka failed: {Reason}.", exception.Error.Reason);

            return null;
        }
    }

    private async Task HandleWithRetryAsync(ConsumeResult<string, string> result, CancellationToken stoppingToken)
    {
        while (true)
        {
            // A new scope per attempt: after a failed save the DbContext still tracks the rejected rows.
            await using var scope = _scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<IActivityRecordHandler>();

            try
            {
                await handler.HandleAsync(result.Topic, result.Message.Value ?? string.Empty, stoppingToken);

                return;
            }
            catch (InvalidRecordException exception)
            {
                _logger.LogError(
                    exception,
                    "Skipping a record that cannot be applied: {Topic} partition {Partition} offset {Offset}.",
                    result.Topic,
                    result.Partition.Value,
                    result.Offset.Value);

                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(
                    exception,
                    "Applying a record failed; retrying in {Delay}: {Topic} partition {Partition} offset {Offset}.",
                    _options.RetryDelay,
                    result.Topic,
                    result.Partition.Value,
                    result.Offset.Value);
                await Task.Delay(_options.RetryDelay, stoppingToken);
            }
        }
    }
}
