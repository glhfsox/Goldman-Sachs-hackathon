namespace Notifier.Application.Interfaces;

public sealed record OutgoingEvent(string Topic, string Key, string Payload);

public interface IEventPublisher
{
    // Publishes the whole batch at once and waits for every acknowledgement. The result lines up with
    // `events`: null when that record was written, otherwise the error.
    Task<IReadOnlyList<string?>> PublishBatchAsync(IReadOnlyList<OutgoingEvent> events, CancellationToken cancellationToken = default);
}
