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

    // Offsets are committed by hand after the database commit: at-least-once, applied idempotently.
    // Earliest matters for the compacted config.changes: a fresh database reads the whole current state.
    public IConsumer<string, string> Create()
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.Brokers,
            GroupId = _options.GroupId,
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest
        };

        return new ConsumerBuilder<string, string>(config).Build();
    }
}
