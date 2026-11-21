using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.AspNetCore.Mvc.Testing;

using Ingest.Tests.Infrastructure;

namespace Ingest.Tests.Api;

// Full path against real containers: HTTP request -> Ingest -> events.raw record.
[Trait("Category", "Integration")]
public sealed class IngestEndpointTests : IClassFixture<PostgresFixture>, IClassFixture<KafkaFixture>
{
    private readonly PostgresFixture _postgres;
    private readonly KafkaFixture _kafka;

    public IngestEndpointTests(PostgresFixture postgres, KafkaFixture kafka)
    {
        _postgres = postgres;
        _kafka = kafka;
    }

    [Fact]
    public async Task Post_JsonEvent_Returns202AndWritesTheRecordToEventsRaw()
    {
        await EnsureTopicAsync();
        using var factory = CreateFactory(_kafka.Brokers);
        using var client = factory.CreateClient();
        using var consumer = CreateConsumer();

        var response = await client.PostAsync(
            "/ingest/alertmanager-http",
            new StringContent("""{"alertname":"HighErrorRate","status":"firing"}""", Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var record = Consume(consumer, body.GetProperty("event_id").GetString()!);
        Assert.Equal("alertmanager-http", record.Message.Key);
        var value = JsonDocument.Parse(record.Message.Value).RootElement;
        Assert.Equal("alertmanager-http", value.GetProperty("source_id").GetString());
        Assert.Equal("HighErrorRate", value.GetProperty("payload").GetProperty("alertname").GetString());
        Assert.EndsWith("Z", value.GetProperty("received_at").GetString());
    }

    [Fact]
    public async Task Post_LogLine_WritesStringPayload()
    {
        await EnsureTopicAsync();
        using var factory = CreateFactory(_kafka.Brokers);
        using var client = factory.CreateClient();
        using var consumer = CreateConsumer();

        var response = await client.PostAsync(
            "/ingest/pricing-logs-http",
            new StringContent("ERROR pricing-engine cache miss", Encoding.UTF8, "text/plain"));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var record = Consume(consumer, body.GetProperty("event_id").GetString()!);
        var value = JsonDocument.Parse(record.Message.Value).RootElement;
        Assert.Equal("ERROR pricing-engine cache miss", value.GetProperty("payload").GetString());
    }

    [Fact]
    public async Task Post_AnyContentType_IsAccepted()
    {
        await EnsureTopicAsync();
        using var factory = CreateFactory(_kafka.Brokers);
        using var client = factory.CreateClient();

        // The body decides the payload, not the Content-Type (curl -d sends a form type).
        var response = await client.PostAsync(
            "/ingest/pricing-logs-http",
            new StringContent("ERROR cache miss", Encoding.UTF8, "application/x-www-form-urlencoded"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Theory]
    [InlineData("/ingest/unknown", "{}", HttpStatusCode.NotFound)]
    [InlineData("/ingest/workflows-kafka", "{}", HttpStatusCode.NotFound)]
    public async Task Post_UnusableSource_Returns404(string path, string content, HttpStatusCode expected)
    {
        using var factory = CreateFactory(_kafka.Brokers);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(path, new StringContent(content, Encoding.UTF8, "application/json"));

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Post_BodyOver64KiB_Returns413()
    {
        using var factory = CreateFactory(_kafka.Brokers);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/ingest/pricing-logs-http",
            new StringContent(new string('x', 64 * 1024 + 1), Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Post_KafkaUnreachable_Returns503()
    {
        // Nothing listens on this port: Kafka can never confirm, so Ingest must not answer 202.
        using var factory = CreateFactory("localhost:1");
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/ingest/alertmanager-http",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private WebApplicationFactory<Program> CreateFactory(string brokers)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DATABASE_URL", _postgres.DatabaseUrl);
            builder.UseSetting("KAFKA_BROKERS", brokers);
        });
    }

    private async Task EnsureTopicAsync()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _kafka.Brokers }).Build();

        try
        {
            await admin.CreateTopicsAsync([new TopicSpecification { Name = "events.raw", NumPartitions = 1 }]);
        }
        catch (CreateTopicsException exception) when (exception.Results.All(result => result.Error.Code == ErrorCode.TopicAlreadyExists))
        {
        }
    }

    private IConsumer<string, string> CreateConsumer()
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

    private static ConsumeResult<string, string> Consume(IConsumer<string, string> consumer, string eventId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            var result = consumer.Consume(TimeSpan.FromSeconds(1));

            if (result is not null && result.Message.Value.Contains(eventId))
            {
                return result;
            }
        }

        throw new Xunit.Sdk.XunitException($"No record with event_id {eventId} on events.raw within 30 s.");
    }
}
