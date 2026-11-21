using System.Text;
using System.Text.Json;

using Ingest.Application.Exceptions;
using Ingest.Application.Interfaces;
using Ingest.Application.Services;
using Ingest.Domain.Models;
using Ingest.Domain.ValueObjects;

namespace Ingest.Tests.Application;

public sealed class IngestServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 10, 15, 2, 123, TimeSpan.Zero);

    private readonly FakeRegistry _registry = new();
    private readonly FakePublisher _publisher = new();

    [Fact]
    public async Task IngestHttpAsync_JsonBody_PublishesParsedPayloadWithStamps()
    {
        _registry.Add("alertmanager-http", SourceKind.Http, enabled: true);

        var response = await CreateService().IngestHttpAsync("alertmanager-http", Bytes("""{"alertname":"HighErrorRate"}"""));

        var published = Assert.Single(_publisher.Published);
        Assert.Equal(response.EventId, published.EventId);
        Assert.NotEqual(Guid.Empty, response.EventId);
        Assert.Equal("alertmanager-http", published.SourceId);
        Assert.Equal(Now, published.ReceivedAt);
        Assert.Equal(JsonValueKind.Object, published.Payload.ValueKind);
        Assert.Equal("HighErrorRate", published.Payload.GetProperty("alertname").GetString());
    }

    [Fact]
    public async Task IngestHttpAsync_LogLine_PublishesStringPayload()
    {
        _registry.Add("pricing-logs-http", SourceKind.Http, enabled: true);

        await CreateService().IngestHttpAsync("pricing-logs-http", Bytes("2026-11-21 ERROR pricing-engine cache miss"));

        var published = Assert.Single(_publisher.Published);
        Assert.Equal(JsonValueKind.String, published.Payload.ValueKind);
        Assert.Equal("2026-11-21 ERROR pricing-engine cache miss", published.Payload.GetString());
    }

    [Fact]
    public async Task IngestHttpAsync_BrokenJson_PublishesBodyAsString()
    {
        _registry.Add("alertmanager-http", SourceKind.Http, enabled: true);

        await CreateService().IngestHttpAsync("alertmanager-http", Bytes("""{"alertname": """));

        Assert.Equal("""{"alertname": """, Assert.Single(_publisher.Published).Payload.GetString());
    }

    [Fact]
    public async Task IngestHttpAsync_InvalidUtf8_ReplacesBadBytes()
    {
        _registry.Add("pricing-logs-http", SourceKind.Http, enabled: true);

        await CreateService().IngestHttpAsync("pricing-logs-http", new byte[] { 0x61, 0xFF, 0x62 });

        Assert.Equal("a�b", Assert.Single(_publisher.Published).Payload.GetString());
    }

    [Fact]
    public async Task IngestHttpAsync_TwoEvents_GetDifferentIds()
    {
        _registry.Add("alertmanager-http", SourceKind.Http, enabled: true);
        var service = CreateService();

        var first = await service.IngestHttpAsync("alertmanager-http", Bytes("{}"));
        var second = await service.IngestHttpAsync("alertmanager-http", Bytes("{}"));

        Assert.NotEqual(first.EventId, second.EventId);
    }

    [Theory]
    [InlineData("unknown", false)]
    [InlineData("off-http", true)]
    [InlineData("workflows-kafka", true)]
    public async Task IngestHttpAsync_UnusableSource_ThrowsNotFoundAndPublishesNothing(string sourceId, bool registered)
    {
        if (registered)
        {
            _registry.Add("off-http", SourceKind.Http, enabled: false);
            _registry.Add("workflows-kafka", SourceKind.Kafka, enabled: true, topic: "source.workflows");
        }

        var exception = await Assert.ThrowsAsync<SourceNotFoundException>(
            () => CreateService().IngestHttpAsync(sourceId, Bytes("{}")));

        Assert.Equal(404, exception.StatusCode);
        Assert.Empty(_publisher.Published);
    }

    [Fact]
    public async Task IngestHttpAsync_BodyOver64KiB_ThrowsTooLarge()
    {
        _registry.Add("alertmanager-http", SourceKind.Http, enabled: true);

        var exception = await Assert.ThrowsAsync<EventTooLargeException>(
            () => CreateService().IngestHttpAsync("alertmanager-http", new byte[IngestService.MaxBodyBytes + 1]));

        Assert.Equal(413, exception.StatusCode);
        Assert.Empty(_publisher.Published);
    }

    [Fact]
    public async Task IngestHttpAsync_BodyOfExactly64KiB_IsAccepted()
    {
        _registry.Add("pricing-logs-http", SourceKind.Http, enabled: true);

        await CreateService().IngestHttpAsync("pricing-logs-http", new byte[IngestService.MaxBodyBytes]);

        Assert.Single(_publisher.Published);
    }

    [Fact]
    public async Task IngestHttpAsync_KafkaNotConfirmed_ThrowsServiceUnavailable()
    {
        _registry.Add("alertmanager-http", SourceKind.Http, enabled: true);
        _publisher.Failure = new EventPublishFailedException(new InvalidOperationException("broker down"));

        var exception = await Assert.ThrowsAsync<EventPublishFailedException>(
            () => CreateService().IngestHttpAsync("alertmanager-http", Bytes("{}")));

        Assert.Equal(503, exception.StatusCode);
    }

    private IngestService CreateService()
    {
        return new IngestService(_registry, _publisher, new FixedTimeProvider(Now));
    }

    private static byte[] Bytes(string text)
    {
        return Encoding.UTF8.GetBytes(text);
    }

    private sealed class FakeRegistry : ISourceRegistry
    {
        private readonly Dictionary<string, Source> _sources = [];

        public void Add(string id, SourceKind kind, bool enabled, string? topic = null)
        {
            _sources[id] = Source.Create(id, kind, new SourceConfig(topic, null), enabled, Now);
        }

        public Source? Find(string id)
        {
            return _sources.GetValueOrDefault(id);
        }

        public IReadOnlyList<Source> GetEnabled()
        {
            return _sources.Values.Where(source => source.Enabled).ToList();
        }

        public Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakePublisher : IRawEventPublisher
    {
        public List<RawEvent> Published { get; } = [];

        public Exception? Failure { get; set; }

        public Task PublishAsync(RawEvent rawEvent, CancellationToken cancellationToken = default)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Published.Add(rawEvent);

            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }
    }
}
