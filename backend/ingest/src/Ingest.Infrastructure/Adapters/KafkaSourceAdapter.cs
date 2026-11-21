using Confluent.Kafka;

using Microsoft.Extensions.Logging;

using Ingest.Application.Exceptions;
using Ingest.Application.Interfaces;
using Ingest.Domain.Services;
using Ingest.Domain.ValueObjects;

namespace Ingest.Infrastructure.Adapters;

// Reads `source.*` topics of one sources cluster and republishes each message to events.raw.
public sealed class KafkaSourceAdapter : ISourceAdapter
{
    public const string GroupId = "ingest";
    public const int DefaultMaxInFlight = 1000;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly string _brokers;
    private readonly IRawEventPublisher _publisher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<KafkaSourceAdapter> _logger;
    private readonly SemaphoreSlim _inFlight;
    private readonly int _maxInFlight;
    private readonly OffsetTracker _tracker = new();

    private volatile IReadOnlyDictionary<string, string> _topicToSourceId = new Dictionary<string, string>();
    private int _topicsVersion;

    public KafkaSourceAdapter(
        string brokers,
        IRawEventPublisher publisher,
        TimeProvider timeProvider,
        ILogger<KafkaSourceAdapter> logger,
        int maxInFlight = DefaultMaxInFlight)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokers);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxInFlight, 1);

        _brokers = brokers;
        _publisher = publisher;
        _timeProvider = timeProvider;
        _logger = logger;
        _maxInFlight = maxInFlight;
        _inFlight = new SemaphoreSlim(maxInFlight, maxInFlight);
    }

    public void SetSources(IReadOnlyDictionary<string, string> topicToSourceId)
    {
        ArgumentNullException.ThrowIfNull(topicToSourceId);

        var topicsChanged = !_topicToSourceId.Keys.ToHashSet().SetEquals(topicToSourceId.Keys);

        _topicToSourceId = topicToSourceId;

        if (topicsChanged)
        {
            Interlocked.Increment(ref _topicsVersion);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        // Consume blocks; hand control back so the caller can carry on.
        await Task.Yield();

        // The key is not read: the source's key is for its own ordering only.
        using var consumer = new ConsumerBuilder<Ignore, byte[]>(new ConsumerConfig
        {
            BootstrapServers = _brokers,
            GroupId = GroupId,
            // A topic's first read starts at its beginning, so nothing a source sent is skipped.
            AutoOffsetReset = AutoOffsetReset.Earliest,
            // Commit only after Kafka confirmed the republished record.
            EnableAutoCommit = false
        })
            .SetPartitionsRevokedHandler((revoking, partitions) =>
            {
                // Confirmed messages are committed before another consumer takes the partitions.
                Commit(revoking);

                foreach (var partition in partitions)
                {
                    _tracker.Forget(partition.TopicPartition);
                }
            })
            .SetPartitionsLostHandler((_, partitions) =>
            {
                foreach (var partition in partitions)
                {
                    _tracker.Forget(partition.TopicPartition);
                }
            })
            .Build();

        var appliedVersion = -1;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                appliedVersion = ApplySubscription(consumer, appliedVersion);

                await PollOnceAsync(consumer, cancellationToken);
                Commit(consumer);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            await DrainAsync();
            Commit(consumer);
            consumer.Close();
        }
    }

    // Subscribing from the loop thread keeps every consumer call on one thread.
    private int ApplySubscription(IConsumer<Ignore, byte[]> consumer, int appliedVersion)
    {
        var version = Volatile.Read(ref _topicsVersion);

        if (version == appliedVersion)
        {
            return appliedVersion;
        }

        var topics = _topicToSourceId.Keys.Order(StringComparer.Ordinal).ToList();

        if (topics.Count == 0)
        {
            consumer.Unsubscribe();
        }
        else
        {
            consumer.Subscribe(topics);
        }

        _logger.LogInformation("Reading topics {Topics} from {Brokers}", string.Join(", ", topics), _brokers);

        return version;
    }

    private async Task PollOnceAsync(IConsumer<Ignore, byte[]> consumer, CancellationToken cancellationToken)
    {
        ConsumeResult<Ignore, byte[]>? result;

        try
        {
            result = consumer.Consume(TimeSpan.FromMilliseconds(200));
        }
        catch (ConsumeException exception)
        {
            // A topic may not exist yet while the sources cluster starts; keep trying.
            _logger.LogWarning("Cannot read from {Brokers}: {Reason}", _brokers, exception.Error.Reason);
            await Task.Delay(RetryDelay, cancellationToken);

            return;
        }

        if (result?.Message is null)
        {
            return;
        }

        var partition = result.TopicPartition;
        var offset = result.Offset.Value;
        _tracker.Register(partition, offset);

        // A null value or a topic that no longer has a source has nothing to publish.
        if (result.Message.Value is null || !_topicToSourceId.TryGetValue(partition.Topic, out var sourceId))
        {
            _tracker.Confirm(partition, offset);

            return;
        }

        var rawEvent = new RawEvent(
            KafkaEventId.Create(sourceId, partition.Topic, partition.Partition.Value, offset),
            sourceId,
            _timeProvider.GetUtcNow(),
            PayloadParser.Parse(result.Message.Value));

        // Waiting here is the back-pressure: no more than _maxInFlight records wait for Kafka.
        await _inFlight.WaitAsync(cancellationToken);

        _ = PublishUntilConfirmedAsync(rawEvent, partition, offset, cancellationToken);
    }

    // A failed publish is retried and never skipped: the offset stays uncommitted until Kafka confirms.
    private async Task PublishUntilConfirmedAsync(
        RawEvent rawEvent,
        TopicPartition partition,
        long offset,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                try
                {
                    await _publisher.PublishAsync(rawEvent, cancellationToken);
                    _tracker.Confirm(partition, offset);

                    return;
                }
                catch (EventPublishFailedException exception)
                {
                    _logger.LogWarning(exception, "Cannot publish {TopicPartitionOffset}; retrying", $"{partition}@{offset}");
                    await Task.Delay(RetryDelay, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown: the offset stays uncommitted and the message is read again next start.
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Giving up on {TopicPartitionOffset}; it is read again after a restart", $"{partition}@{offset}");
        }
        finally
        {
            _inFlight.Release();
        }
    }

    private void Commit(IConsumer<Ignore, byte[]> consumer)
    {
        var offsets = _tracker.TakeCommittable();

        if (offsets.Count == 0)
        {
            return;
        }

        try
        {
            consumer.Commit(offsets);
        }
        catch (KafkaException exception)
        {
            // After a rebalance the partition is gone; its messages are read again by the new owner.
            _logger.LogWarning("Cannot commit offsets: {Reason}", exception.Error.Reason);
        }
    }

    // Takes every permit back, which waits for the publishes still in flight.
    private async Task DrainAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            for (var index = 0; index < _maxInFlight; index++)
            {
                await _inFlight.WaitAsync(timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Stopped with publishes still in flight; they are read again after a restart");
        }
    }
}
