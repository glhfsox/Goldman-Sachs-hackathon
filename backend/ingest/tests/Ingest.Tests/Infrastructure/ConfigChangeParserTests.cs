using Ingest.Domain.Models;
using Ingest.Domain.ValueObjects;
using Ingest.Infrastructure.Kafka;

namespace Ingest.Tests.Infrastructure;

public sealed class ConfigChangeParserTests
{
    [Fact]
    public void Parse_KafkaSourceRecord_BuildsRequestFromBodyAndRecordTimestamp()
    {
        var result = ConfigChangeParser.Parse("""
            {"kind":"source","id":"workflows-kafka","updated_at":"2026-11-20T09:30:00.000Z","deleted":false,
             "body":{"id":"workflows-kafka","kind":"kafka","config":{"topic":"source.workflows","brokers":"h:9092"},"enabled":false}}
            """);

        Assert.Equal(ConfigChangeOutcome.Source, result.Outcome);
        var request = result.Request!;
        Assert.Equal("workflows-kafka", request.Id);
        Assert.Equal(SourceKind.Kafka, request.Kind);
        Assert.Equal(new SourceConfig("source.workflows", "h:9092"), request.Config);
        Assert.False(request.Enabled);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 9, 30, 0, TimeSpan.Zero), request.UpdatedAt);
        Assert.Equal(TimeSpan.Zero, request.UpdatedAt.Offset);
    }

    [Fact]
    public void Parse_HttpSourceWithoutConfig_UsesEmptyConfig()
    {
        var result = ConfigChangeParser.Parse("""
            {"kind":"source","updated_at":"2026-11-20T09:30:00Z","body":{"id":"h","kind":"http","enabled":true}}
            """);

        Assert.Equal(SourceConfig.Empty, result.Request!.Config);
    }

    [Theory]
    [InlineData("""{"kind":"rule","id":"r","updated_at":"2026-11-20T09:30:00Z","body":{"id":"r"}}""")]
    [InlineData("""{"kind":"source","id":"s","updated_at":"2026-11-20T09:30:00Z","deleted":true,"body":null}""")]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_NotForIngest_IsIgnored(string? value)
    {
        Assert.Equal(ConfigChangeOutcome.Ignored, ConfigChangeParser.Parse(value).Outcome);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1]")]
    [InlineData("""{"kind":"source","body":{"id":"s","kind":"http","enabled":true}}""")]
    [InlineData("""{"kind":"source","updated_at":"yesterday","body":{"id":"s","kind":"http","enabled":true}}""")]
    [InlineData("""{"kind":"source","updated_at":"2026-11-20T09:30:00Z","body":null}""")]
    [InlineData("""{"kind":"source","updated_at":"2026-11-20T09:30:00Z","body":{"kind":"http","enabled":true}}""")]
    [InlineData("""{"kind":"source","updated_at":"2026-11-20T09:30:00Z","body":{"id":"s","kind":"rabbit","enabled":true}}""")]
    [InlineData("""{"kind":"source","updated_at":"2026-11-20T09:30:00Z","body":{"id":"s","kind":"http"}}""")]
    [InlineData("""{"kind":"source","updated_at":"2026-11-20T09:30:00Z","body":{"id":"s","kind":"kafka","config":{},"enabled":true}}""")]
    public void Parse_BadRecord_IsInvalid(string value)
    {
        var result = ConfigChangeParser.Parse(value);

        Assert.Equal(ConfigChangeOutcome.Invalid, result.Outcome);
        Assert.NotNull(result.Reason);
    }
}
