using Confluent.Kafka;

using Ingest.Infrastructure.Adapters;

namespace Ingest.Tests.Adapters;

public sealed class OffsetTrackerTests
{
    private static readonly TopicPartition Partition = new("source.workflows", 0);

    [Fact]
    public void TakeCommittable_AllConfirmedInOrder_CommitsTheNextOffset()
    {
        var tracker = new OffsetTracker();
        Register(tracker, 10, 11, 12);
        Confirm(tracker, 10, 11, 12);

        var commit = Assert.Single(tracker.TakeCommittable());

        Assert.Equal(new TopicPartitionOffset(Partition, 13), commit);
    }

    [Fact]
    public void TakeCommittable_LaterMessageConfirmedFirst_StopsBeforeTheOneInFlight()
    {
        var tracker = new OffsetTracker();
        Register(tracker, 10, 11, 12);
        Confirm(tracker, 11, 12);

        var commit = Assert.Single(tracker.TakeCommittable());

        // 10 is still in flight, so a crash must read 10 again.
        Assert.Equal(10, commit.Offset.Value);
    }

    [Fact]
    public void TakeCommittable_AfterTheGapCloses_CommitsPastIt()
    {
        var tracker = new OffsetTracker();
        Register(tracker, 10, 11);
        Confirm(tracker, 11);
        tracker.TakeCommittable();
        Confirm(tracker, 10);

        var commit = Assert.Single(tracker.TakeCommittable());

        Assert.Equal(12, commit.Offset.Value);
    }

    [Fact]
    public void TakeCommittable_NothingNewOrNothingConfirmed_ReturnsNothing()
    {
        var tracker = new OffsetTracker();
        Register(tracker, 0);

        Assert.Equal(0, tracker.TakeCommittable().Single().Offset.Value);
        Assert.Empty(tracker.TakeCommittable());
    }

    [Fact]
    public void Forget_DropsThePartitionState()
    {
        var tracker = new OffsetTracker();
        Register(tracker, 5);
        tracker.Forget(Partition);

        Assert.Empty(tracker.TakeCommittable());
    }

    private static void Register(OffsetTracker tracker, params long[] offsets)
    {
        foreach (var offset in offsets)
        {
            tracker.Register(Partition, offset);
        }
    }

    private static void Confirm(OffsetTracker tracker, params long[] offsets)
    {
        foreach (var offset in offsets)
        {
            tracker.Confirm(Partition, offset);
        }
    }
}
