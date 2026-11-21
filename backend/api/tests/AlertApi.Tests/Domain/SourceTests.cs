using AlertApi.Domain.Models;
using AlertApi.Domain.ValueObjects;

namespace AlertApi.Tests.Domain;

public sealed class SourceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-21T10:00:00Z");

    [Fact]
    public void Create_KafkaSourceWithoutTopic_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(
            () => Source.Create("trades-kafka", SourceKind.Kafka, SourceConfig.Empty, enabled: true, Now));
    }

    [Fact]
    public void Create_HttpSourceWithoutConfig_IsValid()
    {
        var source = Source.Create("alerts-http", SourceKind.Http, SourceConfig.Empty, enabled: true, Now);

        Assert.Equal("alerts-http", source.Id);
        Assert.True(source.Enabled);
    }

    [Fact]
    public void Update_ChangesEveryFieldAndTheTimestamp()
    {
        var source = Source.Create("trades-kafka", SourceKind.Kafka, new SourceConfig("source.trades", null), true, Now);
        var later = Now.AddMinutes(1);

        source.Update(SourceKind.Kafka, new SourceConfig("source.trades.v2", "other:9092"), enabled: false, later);

        Assert.Equal("source.trades.v2", source.Config.Topic);
        Assert.Equal("other:9092", source.Config.Brokers);
        Assert.False(source.Enabled);
        Assert.Equal(later, source.UpdatedAt);
    }
}
