using Ingest.Domain.Services;

namespace Ingest.Tests.Domain;

public sealed class KafkaEventIdTests
{
    [Fact]
    public void Create_SameMessage_GivesTheSameIdAsAnIndependentUuidV5()
    {
        // Computed with Python: uuid.uuid5(UUID("6f1c2b7e-3a45-4d8e-9b0a-5c7e2d4f8a11"), "workflows-kafka/source.workflows/2/41").
        var id = KafkaEventId.Create("workflows-kafka", "source.workflows", 2, 41);

        Assert.Equal(Guid.Parse("d99d71ff-6688-5312-a9bb-ef37d824e3b4"), id);
        Assert.Equal(5, id.Version);
    }

    [Fact]
    public void Create_DifferentOffsetPartitionOrSource_GivesDifferentIds()
    {
        var ids = new[]
        {
            KafkaEventId.Create("a", "t", 0, 1),
            KafkaEventId.Create("a", "t", 0, 2),
            KafkaEventId.Create("a", "t", 1, 1),
            KafkaEventId.Create("b", "t", 0, 1)
        };

        Assert.Equal(4, ids.Distinct().Count());
    }
}
