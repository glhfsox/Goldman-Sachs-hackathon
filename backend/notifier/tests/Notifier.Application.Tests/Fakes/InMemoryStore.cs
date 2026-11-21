using Notifier.Domain.Models;

namespace Notifier.Application.Tests.Fakes;

// One shared store behind every fake repository, like one DbContext behind the real ones.
public sealed class InMemoryStore
{
    public List<Notification> Notifications { get; } = [];

    public List<Alert> Alerts { get; } = [];

    public List<object> Config { get; } = [];

    public List<RateCounter> RateCounters { get; } = [];

    public List<OutboxMessage> Outbox { get; } = [];

    public List<ProcessedChange> ProcessedChanges { get; } = [];

    public IEnumerable<T> Copies<T>() => Config.OfType<T>();
}
