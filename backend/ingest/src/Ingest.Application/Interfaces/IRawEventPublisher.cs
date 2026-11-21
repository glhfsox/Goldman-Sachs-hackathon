using Ingest.Domain.ValueObjects;

namespace Ingest.Application.Interfaces;

public interface IRawEventPublisher
{
    // Completes only after Kafka has confirmed the record. Throws EventPublishFailedException otherwise.
    Task PublishAsync(RawEvent rawEvent, CancellationToken cancellationToken = default);
}
