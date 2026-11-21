namespace AlertApi.Application.Conditions;

// The envelope fields of contracts/events.md §3. A path that starts with one is read from the envelope, any other from `raw`
// (contracts/rules.md §1); the processor keeps the same list.
public static class EnvelopeFields
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>
    {
        "event_id", "source", "type", "severity", "env", "service", "ts", "dedup_hints", "mapped", "mapping_id", "received_at", "raw"
    };
}
