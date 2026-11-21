using System.Text.Json;

using Ingest.Domain.ValueObjects;
using Ingest.Infrastructure.Kafka;

namespace Ingest.Tests.Infrastructure;

public sealed class RawEventSerializerTests
{
    [Fact]
    public void Serialize_MatchesTheEventsRawContract()
    {
        var eventId = Guid.Parse("0193e5a0-7c1b-7c3a-9d2e-4f5a6b7c8d9e");
        var payload = JsonDocument.Parse("""{"type":"WORKFLOW_FAILED"}""").RootElement;
        var rawEvent = new RawEvent(eventId, "workflows-kafka", new DateTimeOffset(2026, 11, 20, 10, 15, 2, 123, TimeSpan.Zero), payload);

        var json = JsonDocument.Parse(RawEventSerializer.Serialize(rawEvent)).RootElement;

        Assert.Equal(
            ["event_id", "source_id", "received_at", "payload"],
            json.EnumerateObject().Select(property => property.Name));
        Assert.Equal(eventId.ToString(), json.GetProperty("event_id").GetString());
        Assert.Equal("workflows-kafka", json.GetProperty("source_id").GetString());
        Assert.Equal("2026-11-20T10:15:02.123Z", json.GetProperty("received_at").GetString());
        Assert.Equal("WORKFLOW_FAILED", json.GetProperty("payload").GetProperty("type").GetString());
    }

    [Fact]
    public void Serialize_StringPayload_StaysAJsonString()
    {
        var payload = JsonSerializer.SerializeToElement("a log line");
        var rawEvent = new RawEvent(Guid.NewGuid(), "pricing-logs-http", DateTimeOffset.UtcNow, payload);

        var json = JsonDocument.Parse(RawEventSerializer.Serialize(rawEvent)).RootElement;

        Assert.Equal(JsonValueKind.String, json.GetProperty("payload").ValueKind);
    }
}
