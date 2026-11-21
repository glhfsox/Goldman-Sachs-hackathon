using Confluent.Kafka;
using Microsoft.Extensions.Options;

using Notifier.Application.Interfaces;

namespace Notifier.Infrastructure.Messaging;

public sealed class KafkaEventPublisher : IEventPublisher, IDisposable
{
    private readonly IProducer<string, string> _producer;

    public KafkaEventPublisher(IOptions<KafkaOptions> options)
    {
        var config = new ProducerConfig
        {
            BootstrapServers = options.Value.Brokers,
            Acks = Acks.All,
            EnableIdempotence = true,
            // A few milliseconds of waiting lets one request carry many records.
            LingerMs = 5
        };
        _producer = new ProducerBuilder<string, string>(config).Build();
    }

    // Keyed by alert id (contracts/services.md), so every record of one alert stays in order on one partition;
    // the idempotent producer keeps that order with several requests in flight.
    public async Task<IReadOnlyList<string?>> PublishBatchAsync(
        IReadOnlyList<OutgoingEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        var results = new Task<string?>[events.Count];
        for (var index = 0; index < events.Count; index++)
        {
            var outgoing = events[index];
            var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            results[index] = result.Task;
            try
            {
                _producer.Produce(
                    outgoing.Topic,
                    new Message<string, string> { Key = outgoing.Key, Value = outgoing.Payload },
                    report => result.TrySetResult(report.Error.IsError ? report.Error.Reason : null));
            }
            catch (KafkaException exception)
            {
                result.TrySetResult(exception.Error.Reason);
            }
        }

        return await Task.WhenAll(results).WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}
