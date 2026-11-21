using Ingest.Domain.Models;
using Ingest.Domain.ValueObjects;

namespace Ingest.Tests.Domain;

public sealed class SourceTests
{
    [Fact]
    public void Create_KafkaWithoutTopic_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            Source.Create("x-kafka", SourceKind.Kafka, SourceConfig.Empty, enabled: true, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Create_HttpWithEmptyConfig_Succeeds()
    {
        var source = Source.Create("x-http", SourceKind.Http, SourceConfig.Empty, enabled: true, DateTimeOffset.UtcNow);

        Assert.Equal("x-http", source.Id);
    }

    [Fact]
    public void Create_BlankId_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            Source.Create(" ", SourceKind.Http, SourceConfig.Empty, enabled: true, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ApplyChange_NewerRecord_ReplacesAllFields()
    {
        var source = Source.Create("x-kafka", SourceKind.Kafka, new SourceConfig("a", null), enabled: true, At(1));

        var applied = source.ApplyChange(SourceKind.Kafka, new SourceConfig("b", "h:9092"), enabled: false, At(2));

        Assert.True(applied);
        Assert.Equal(new SourceConfig("b", "h:9092"), source.Config);
        Assert.False(source.Enabled);
        Assert.Equal(At(2), source.UpdatedAt);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void ApplyChange_EqualOrOlderRecord_IsIgnored(int seconds)
    {
        var source = Source.Create("x-kafka", SourceKind.Kafka, new SourceConfig("a", null), enabled: true, At(1));

        var applied = source.ApplyChange(SourceKind.Kafka, new SourceConfig("b", null), enabled: false, At(seconds));

        Assert.False(applied);
        Assert.Equal("a", source.Config.Topic);
        Assert.True(source.Enabled);
    }

    [Fact]
    public void ApplyChange_KafkaWithoutTopic_ThrowsAndKeepsTheRow()
    {
        var source = Source.Create("x-http", SourceKind.Http, SourceConfig.Empty, enabled: true, At(1));

        Assert.ThrowsAny<ArgumentException>(() =>
            source.ApplyChange(SourceKind.Kafka, SourceConfig.Empty, enabled: true, At(2)));
        Assert.Equal(SourceKind.Http, source.Kind);
        Assert.Equal(At(1), source.UpdatedAt);
    }

    private static DateTimeOffset At(int seconds)
    {
        return new DateTimeOffset(2026, 11, 21, 0, 0, seconds, TimeSpan.Zero);
    }
}
