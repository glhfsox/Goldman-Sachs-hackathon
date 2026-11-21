using Confluent.Kafka;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Ingest.Application.Interfaces;

namespace Ingest.Infrastructure.Kafka;

// Keeps the sources table in step with the api's `config.changes` topic (contracts/events.md section 6).
public sealed class ConfigChangesConsumer : BackgroundService
{
    public const string Topic = "config.changes";
    public const string GroupId = "ingest";

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _brokers;
    private readonly ILogger<ConfigChangesConsumer> _logger;

    public ConfigChangesConsumer(
        IServiceScopeFactory scopeFactory,
        string brokers,
        ILogger<ConfigChangesConsumer> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokers);

        _scopeFactory = scopeFactory;
        _brokers = brokers;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Consume blocks; hand control back so the host can finish starting.
        await Task.Yield();

        // A shared group splits the partitions between replicas; the registry refresher spreads the result.
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = _brokers,
            GroupId = GroupId,
            // A new replica with an empty group reads the compacted topic from the start.
            AutoOffsetReset = AutoOffsetReset.Earliest,
            // Commit only after the database save, so a crash replays the record.
            EnableAutoCommit = false
        }).Build();

        consumer.Subscribe(Topic);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await ConsumeOneAsync(consumer, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            consumer.Close();
        }
    }

    private async Task ConsumeOneAsync(IConsumer<string, string> consumer, CancellationToken stoppingToken)
    {
        ConsumeResult<string, string>? result;

        try
        {
            result = consumer.Consume(TimeSpan.FromSeconds(1));
        }
        catch (ConsumeException exception)
        {
            // The topic may not exist yet while Kafka starts; keep trying.
            _logger.LogWarning("Cannot read {Topic}: {Reason}", Topic, exception.Error.Reason);
            await Task.Delay(RetryDelay, stoppingToken);

            return;
        }

        if (result?.Message is null)
        {
            return;
        }

        await HandleUntilStoredAsync(result, stoppingToken);

        consumer.Commit(result);
    }

    // A record that cannot be saved (database down) is retried, never skipped: skipping would lose the change.
    private async Task HandleUntilStoredAsync(ConsumeResult<string, string> result, CancellationToken stoppingToken)
    {
        var parsed = ConfigChangeParser.Parse(result.Message.Value);

        if (parsed.Outcome == ConfigChangeOutcome.Ignored)
        {
            _logger.LogDebug("Skipped {Topic} record at {Offset}: {Reason}", Topic, result.TopicPartitionOffset, parsed.Reason);

            return;
        }

        if (parsed.Outcome == ConfigChangeOutcome.Invalid)
        {
            _logger.LogWarning("Dropped bad {Topic} record at {Offset}: {Reason}", Topic, result.TopicPartitionOffset, parsed.Reason);

            return;
        }

        while (true)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<ISourceService>();

                var applied = await service.ApplyChangeAsync(parsed.Request!, stoppingToken);

                _logger.LogInformation(
                    "Source {SourceId} {Result} from {Topic} at {Offset}",
                    parsed.Request!.Id,
                    applied ? "updated" : "ignored (not newer)",
                    Topic,
                    result.TopicPartitionOffset);

                return;
            }
            catch (ArgumentException exception)
            {
                // The domain rejected values the parser could not see; retrying cannot help.
                _logger.LogWarning(exception, "Dropped bad {Topic} record at {Offset}", Topic, result.TopicPartitionOffset);

                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Cannot save {Topic} record at {Offset}; retrying", Topic, result.TopicPartitionOffset);
                await Task.Delay(RetryDelay, stoppingToken);
            }
        }
    }
}
