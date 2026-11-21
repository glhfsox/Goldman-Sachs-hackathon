using Notifier.Application.Interfaces;

namespace Notifier.Application.Tests.Fakes;

// Publishes everything except records whose payload contains FailWhenPayloadContains.
public sealed class FakePublisher : IEventPublisher
{
    public List<OutgoingEvent> Published { get; } = [];

    public string? FailWhenPayloadContains { get; set; }

    public Task<IReadOnlyList<string?>> PublishBatchAsync(IReadOnlyList<OutgoingEvent> events, CancellationToken cancellationToken = default)
    {
        var results = new List<string?>();
        foreach (var outgoing in events)
        {
            if (FailWhenPayloadContains is { } marker && outgoing.Payload.Contains(marker))
            {
                results.Add("broker unavailable");
                continue;
            }

            Published.Add(outgoing);
            results.Add(null);
        }

        return Task.FromResult<IReadOnlyList<string?>>(results);
    }
}
