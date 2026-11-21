namespace AlertApi.Application.Constants;

// Internal Kafka topics this service reads or writes (contracts/services.md).
public static class Topics
{
    public const string ConfigChanges = "config.changes";
    public const string AlertsChanges = "alerts.changes";
    public const string AlertsTimeline = "alerts.timeline";
    public const string SchemaSamples = "schema.samples";
}
