using System.Text.Json;

using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging.Abstractions;

using Ingest.Application.Interfaces;
using Ingest.Domain.Models;
using Ingest.Domain.Services;
using Ingest.Domain.ValueObjects;
using Ingest.Infrastructure.Adapters;
using Ingest.Infrastructure.Kafka;
using Ingest.Infrastructure.Logging;
using Ingest.Tests.Infrastructure;

namespace Ingest.Tests.Adapters;

// Real Kafka as both clusters: a message on a source topic ends up on events.raw.
[Trait("Category", "Integration")]
public sealed class KafkaSourceManagerTests : IClassFixture<KafkaFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);

    private readonly KafkaFixture _kafka;

    public KafkaSourceManagerTests(KafkaFixture kafka)
    {
        _kafka = kafka;
    }

    [Fact]
    public async Task Manager_ReadsEnabledTopicsAndPicksUpANewSource()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var firstTopic = $"source.first-{suffix}";
        var secondTopic = $"source.second-{suffix}";
        var offTopic = $"source.off-{suffix}";
        await EnsureTopicsAsync("events.raw", firstTopic, secondTopic, offTopic);
        var registry = new FakeRegistry([
            Kafka($"first-{suffix}", firstTopic, enabled: true),
            Kafka($"off-{suffix}", offTopic, enabled: false)
        ]);
        using var publisher = new KafkaRawEventPublisher(_kafka.Brokers);
        var manager = new KafkaSourceManager(
            registry,
            brokers => new KafkaSourceAdapter(brokers, publisher, TimeProvider.System, NullLogger<KafkaSourceAdapter>.Instance, new LogThrottle(TimeSpan.Zero, TimeProvider.System)),
            _kafka.Brokers,
            TimeSpan.FromMilliseconds(300),
            NullLogger<KafkaSourceManager>.Instance);
        using var rawConsumer = CreateRawConsumer();

        await manager.StartAsync(CancellationToken.None);
        await ProduceAsync(offTopic, """{"n":0}""");
        await ProduceAsync(firstTopic, """{"type":"WORKFLOW_FAILED","n":1}""");
        var first = ConsumeUntil(rawConsumer, record => record.GetProperty("source_id").GetString() == $"first-{suffix}");

        // A source added later is read after the next reconcile, without a restart.
        registry.Add(Kafka($"second-{suffix}", secondTopic, enabled: true));
        await ProduceAsync(secondTopic, "plain log line");
        var second = ConsumeUntil(rawConsumer, record => record.GetProperty("source_id").GetString() == $"second-{suffix}");
        await manager.StopAsync(CancellationToken.None);

        Assert.Equal(1, first.GetProperty("payload").GetProperty("n").GetInt32());
        Assert.Equal(
            KafkaEventId.Create($"first-{suffix}", firstTopic, 0, 0).ToString(),
            first.GetProperty("event_id").GetString());
        Assert.Equal("plain log line", second.GetProperty("payload").GetString());
        Assert.EndsWith("Z", second.GetProperty("received_at").GetString());
        Assert.Empty(DrainFor(rawConsumer, $"off-{suffix}"));
    }

    private static Source Kafka(string id, string topic, bool enabled)
    {
        return Source.Create(id, SourceKind.Kafka, new SourceConfig(topic, null), enabled, Now);
    }

    private async Task ProduceAsync(string topic, string value)
    {
        using var producer = new ProducerBuilder<Null, string>(new ProducerConfig { BootstrapServers = _kafka.Brokers }).Build();

        await producer.ProduceAsync(topic, new Message<Null, string> { Value = value });
    }

    private async Task EnsureTopicsAsync(params string[] topics)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _kafka.Brokers }).Build();

        try
        {
            await admin.CreateTopicsAsync(topics.Select(topic => new TopicSpecification { Name = topic, NumPartitions = 1 }));
        }
        catch (CreateTopicsException exception) when (exception.Results.All(result => result.Error.Code == ErrorCode.TopicAlreadyExists))
        {
        }
    }

    private IConsumer<string, string> CreateRawConsumer()
    {
        var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = _kafka.Brokers,
            GroupId = Guid.NewGuid().ToString("N"),
            AutoOffsetReset = AutoOffsetReset.Earliest
        }).Build();
        consumer.Subscribe("events.raw");

        return consumer;
    }

    private static JsonElement ConsumeUntil(IConsumer<string, string> consumer, Func<JsonElement, bool> match)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);

        while (DateTime.UtcNow < deadline)
        {
            var result = consumer.Consume(TimeSpan.FromSeconds(1));

            if (result is not null)
            {
                var record = JsonDocument.Parse(result.Message.Value).RootElement.Clone();

                if (match(record))
                {
                    return record;
                }
            }
        }

        throw new Xunit.Sdk.XunitException("The expected record did not reach events.raw within 60 s.");
    }

    private static List<string> DrainFor(IConsumer<string, string> consumer, string sourceId)
    {
        var found = new List<string>();
        var end = DateTime.UtcNow.AddSeconds(2);

        while (DateTime.UtcNow < end)
        {
            var result = consumer.Consume(TimeSpan.FromMilliseconds(300));

            if (result is not null && result.Message.Value.Contains($"\"{sourceId}\""))
            {
                found.Add(result.Message.Value);
            }
        }

        return found;
    }

    private sealed class FakeRegistry : ISourceRegistry
    {
        private readonly object _lock = new();
        private readonly List<Source> _sources;

        public FakeRegistry(IEnumerable<Source> sources)
        {
            _sources = sources.ToList();
        }

        public void Add(Source source)
        {
            lock (_lock)
            {
                _sources.Add(source);
            }
        }

        public Source? Find(string id)
        {
            lock (_lock)
            {
                return _sources.FirstOrDefault(source => source.Id == id);
            }
        }

        public IReadOnlyList<Source> GetEnabled()
        {
            lock (_lock)
            {
                return _sources.Where(source => source.Enabled).ToList();
            }
        }

        public Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
