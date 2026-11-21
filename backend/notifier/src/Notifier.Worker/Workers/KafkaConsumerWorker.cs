using System.Text.Json;

using Confluent.Kafka;

using Notifier.Application.Exceptions;
using Notifier.Application.Serialization;
using Notifier.Infrastructure.Messaging;

namespace Notifier.Worker.Workers;

// Consume -> handle in a fresh scope -> store the offset only after the database commit (at-least-once);
// stored offsets are committed in the background and on Close.
// Up to MaxBatchSize records already fetched are handled together; if that fails they are handled one by one,
// so one bad record cannot hold back the others.
// A record that fails for a transient reason is read again; one that can never be applied is logged and skipped.
public abstract class KafkaConsumerWorker<TMessage> : BackgroundService
    where TMessage : class
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly KafkaConsumerFactory _consumerFactory;
    private readonly ILogger _logger;

    protected KafkaConsumerWorker(
        IServiceScopeFactory scopeFactory,
        KafkaConsumerFactory consumerFactory,
        ILogger logger)
    {
        _scopeFactory = scopeFactory;
        _consumerFactory = consumerFactory;
        _logger = logger;
    }

    protected abstract string Topic { get; }

    // True for a compacted state topic that must be read whole on every start (config.changes).
    protected virtual bool ReplayFromBeginning => false;

    // How many already-fetched records one HandleBatchAsync call may get. 1 keeps record-at-a-time handling.
    protected virtual int MaxBatchSize => 1;

    protected abstract Task HandleAsync(IServiceProvider services, TMessage message, CancellationToken cancellationToken);

    // Must apply the records in order, all or nothing.
    protected virtual Task HandleBatchAsync(IServiceProvider services, IReadOnlyList<TMessage> messages, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{GetType().Name} sets MaxBatchSize above 1 without overriding HandleBatchAsync");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Consume blocks the thread; yielding first lets the host finish starting the other workers.
        await Task.Yield();

        using var consumer = _consumerFactory.Create(ReplayFromBeginning);
        consumer.Subscribe(Topic);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var batch = new List<ConsumeResult<string, string>>();
                try
                {
                    var first = consumer.Consume(stoppingToken);
                    if (first?.Message is null)
                    {
                        continue;
                    }

                    batch.Add(first);
                    // Only what is already fetched: a batch never waits for more records.
                    while (batch.Count < MaxBatchSize && consumer.Consume(TimeSpan.Zero) is { Message: not null } next)
                    {
                        batch.Add(next);
                    }
                }
                catch (ConsumeException exception)
                {
                    if (batch.Count == 0)
                    {
                        // Typically the topic does not exist yet: kafka-init has not run.
                        _logger.LogWarning("Consuming {Topic} failed: {Reason}", Topic, exception.Error.Reason);
                        await Task.Delay(RetryDelay, stoppingToken);
                        continue;
                    }
                }

                var handled = await HandleRecordsAsync(batch, stoppingToken);
                foreach (var result in batch.Take(handled))
                {
                    consumer.StoreOffset(result);
                }

                if (handled < batch.Count)
                {
                    // Rewind every partition to its first record not handled, so nothing fetched after it is lost.
                    foreach (var partition in batch.Skip(handled).GroupBy(result => result.TopicPartition))
                    {
                        consumer.Seek(partition.MinBy(result => result.Offset.Value)!.TopicPartitionOffset);
                    }

                    await Task.Delay(RetryDelay, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Stopping the {Topic} consumer", Topic);
        }
        finally
        {
            consumer.Close();
        }
    }

    // Returns how many records, from the start of the batch, are done (applied or skipped for good).
    private async Task<int> HandleRecordsAsync(IReadOnlyList<ConsumeResult<string, string>> batch, CancellationToken cancellationToken)
    {
        var messages = batch.Select(result => Deserialize(result.Message.Value)).ToList();
        if (batch.Count > 1)
        {
            var valid = messages.OfType<TMessage>().ToList();
            if (valid.Count == 0
                || await TryHandleAsync(services => HandleBatchAsync(services, valid, cancellationToken), $"a batch of {valid.Count}"))
            {
                return batch.Count;
            }
        }

        for (var index = 0; index < messages.Count; index++)
        {
            if (messages[index] is { } message
                && !await TryHandleAsync(services => HandleAsync(services, message, cancellationToken), "a record"))
            {
                return index;
            }
        }

        return batch.Count;
    }

    // Null for a record to skip: no value, or malformed JSON.
    private TMessage? Deserialize(string? value)
    {
        try
        {
            return value is null ? null : JsonSerializer.Deserialize<TMessage>(value, NotifierJson.Options);
        }
        catch (JsonException exception)
        {
            _logger.LogError(exception, "Skipping a malformed {Topic} record", Topic);

            return null;
        }
    }

    private async Task<bool> TryHandleAsync(Func<IServiceProvider, Task> handle, string what)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            await handle(scope.ServiceProvider);

            return true;
        }
        catch (InvalidAlertChangeException exception)
        {
            _logger.LogError(exception, "Skipping an invalid {Topic} record", Topic);

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Handling {What} from {Topic} failed; it will be retried", what, Topic);

            return false;
        }
    }
}
