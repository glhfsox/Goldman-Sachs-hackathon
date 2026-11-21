using Confluent.Kafka;

namespace Ingest.Infrastructure.Adapters;

// Messages are published concurrently and can finish out of order. A partition's offset may only
// be committed up to the first message that is still in flight, or a crash would lose it.
public sealed class OffsetTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<TopicPartition, PartitionState> _partitions = [];

    public void Register(TopicPartition partition, long offset)
    {
        lock (_lock)
        {
            if (!_partitions.TryGetValue(partition, out var state))
            {
                state = new PartitionState();
                _partitions[partition] = state;
            }

            state.Pending.Add(offset);
            state.NextOffset = Math.Max(state.NextOffset, offset + 1);
        }
    }

    public void Confirm(TopicPartition partition, long offset)
    {
        lock (_lock)
        {
            if (_partitions.TryGetValue(partition, out var state))
            {
                state.Pending.Remove(offset);
            }
        }
    }

    // Offsets to commit now; each partition appears only when its commit position moved.
    public IReadOnlyList<TopicPartitionOffset> TakeCommittable()
    {
        lock (_lock)
        {
            var result = new List<TopicPartitionOffset>();

            foreach (var (partition, state) in _partitions)
            {
                var position = state.Pending.Count > 0 ? state.Pending.Min : state.NextOffset;

                if (position > state.Committed)
                {
                    state.Committed = position;
                    result.Add(new TopicPartitionOffset(partition, position));
                }
            }

            return result;
        }
    }

    // After a rebalance another consumer owns the partition; its state here would be stale.
    public void Forget(TopicPartition partition)
    {
        lock (_lock)
        {
            _partitions.Remove(partition);
        }
    }

    private sealed class PartitionState
    {
        public SortedSet<long> Pending { get; } = [];

        public long NextOffset { get; set; }

        public long Committed { get; set; } = -1;
    }
}
