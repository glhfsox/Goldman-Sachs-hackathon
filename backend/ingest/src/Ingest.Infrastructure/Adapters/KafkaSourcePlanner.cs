using Microsoft.Extensions.Logging;

using Ingest.Domain.Models;

namespace Ingest.Infrastructure.Adapters;

public static class KafkaSourcePlanner
{
    // Brokers -> (topic -> source id) for the enabled kafka sources. A message has no source id,
    // only a topic, so one (brokers, topic) belongs to one source: the first by id wins.
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Plan(
        IEnumerable<Source> sources,
        string defaultBrokers,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultBrokers);
        ArgumentNullException.ThrowIfNull(logger);

        var plan = new Dictionary<string, Dictionary<string, string>>();

        foreach (var source in sources
                     .Where(source => source.Enabled && source.Kind == SourceKind.Kafka)
                     .OrderBy(source => source.Id, StringComparer.Ordinal))
        {
            var brokers = string.IsNullOrWhiteSpace(source.Config.Brokers) ? defaultBrokers : source.Config.Brokers;

            if (!plan.TryGetValue(brokers, out var topics))
            {
                topics = [];
                plan[brokers] = topics;
            }

            if (topics.TryGetValue(source.Config.Topic!, out var owner))
            {
                logger.LogWarning(
                    "Source {SourceId} is not consumed: topic {Topic} on {Brokers} already belongs to source {OwnerId}",
                    source.Id,
                    source.Config.Topic,
                    brokers,
                    owner);

                continue;
            }

            topics[source.Config.Topic!] = source.Id;
        }

        return plan.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyDictionary<string, string>)entry.Value);
    }
}
