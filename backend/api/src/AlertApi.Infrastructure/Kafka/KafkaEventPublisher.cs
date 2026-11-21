using Confluent.Kafka;

using AlertApi.Application.Exceptions;
using AlertApi.Application.Interfaces;

namespace AlertApi.Infrastructure.Kafka;

public sealed class KafkaEventPublisher : IEventPublisher, IDisposable
{
    private readonly IProducer<string, string> _producer;

    public KafkaEventPublisher(string brokers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokers);

        _producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = brokers,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageTimeoutMs = 10000
        }).Build();
    }

    public async Task PublishAsync(
        string topic,
        string key,
        string payload,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _producer.ProduceAsync(topic, new Message<string, string> { Key = key, Value = payload }, cancellationToken);
        }
        catch (KafkaException exception)
        {
            throw new EventPublishFailedException(topic, exception);
        }
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(2));
        _producer.Dispose();
    }
}
