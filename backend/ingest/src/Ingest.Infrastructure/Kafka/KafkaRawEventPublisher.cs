using Confluent.Kafka;

using Ingest.Application.Exceptions;
using Ingest.Application.Interfaces;
using Ingest.Domain.ValueObjects;

namespace Ingest.Infrastructure.Kafka;

public sealed class KafkaRawEventPublisher : IRawEventPublisher, IDisposable
{
    public const string Topic = "events.raw";

    private readonly IProducer<string, string> _producer;

    public KafkaRawEventPublisher(string brokers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokers);

        _producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = brokers,
            Acks = Acks.All,
            EnableIdempotence = true,
            // A dead broker must turn into a 503 within the sender's 5 s timeout.
            MessageTimeoutMs = 4000,
            SocketTimeoutMs = 3000
        }).Build();
    }

    public async Task PublishAsync(RawEvent rawEvent, CancellationToken cancellationToken = default)
    {
        var message = new Message<string, string>
        {
            // The key keeps one source's events in order inside a partition.
            Key = rawEvent.SourceId,
            Value = RawEventSerializer.Serialize(rawEvent)
        };

        try
        {
            await _producer.ProduceAsync(Topic, message, cancellationToken);
        }
        catch (KafkaException exception)
        {
            throw new EventPublishFailedException(exception);
        }
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(2));
        _producer.Dispose();
    }
}
