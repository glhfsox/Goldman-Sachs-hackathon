using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Notifier.Infrastructure.Messaging;

public sealed class KafkaConsumerFactory
{
    private readonly KafkaOptions _options;

    public KafkaConsumerFactory(IOptions<KafkaOptions> options)
    {
        _options = options.Value;
    }

    // An offset is stored by hand only after the database commit, and the stored offsets are committed in the
    // background every 500 ms instead of one broker round trip per record: still at-least-once, applied
    // idempotently. Earliest matters for the compacted config.changes: a fresh database reads the whole state.
    public IConsumer<string, string> Create(bool replayFromBeginning = false)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.Brokers,
            GroupId = _options.GroupId,
            EnableAutoCommit = true,
            EnableAutoOffsetStore = false,
            AutoCommitIntervalMs = 500,
            AutoOffsetReset = AutoOffsetReset.Earliest
        };

        var builder = new ConsumerBuilder<string, string>(config);
        if (replayFromBeginning)
        {
            // A compacted topic is the whole state: read all of it on every assignment, whatever offsets the group
            // committed, so a fresh database (new volume, new replica) is filled again. Applying it twice is a no-op.
            builder.SetPartitionsAssignedHandler((_, partitions) =>
                partitions.Select(partition => new TopicPartitionOffset(partition, Offset.Beginning)));
        }

        return builder.Build();
    }
}
