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
}
