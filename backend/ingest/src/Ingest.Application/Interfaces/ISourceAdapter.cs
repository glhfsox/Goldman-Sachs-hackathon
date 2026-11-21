namespace Ingest.Application.Interfaces;

// A long-running reader of one external system. It only receives, enqueues to events.raw and acks.
public interface ISourceAdapter
{
    // Maps each topic to read to the id of the source that owns it. Safe to call while running.
    void SetSources(IReadOnlyDictionary<string, string> topicToSourceId);

    Task RunAsync(CancellationToken cancellationToken = default);
}
