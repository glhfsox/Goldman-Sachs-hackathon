using System.Globalization;
using System.Text.Json;

using Ingest.Domain.ValueObjects;

namespace Ingest.Infrastructure.Kafka;

public static class RawEventSerializer
{
    // The field names and formats of contracts/events.md section 2.
    public static string Serialize(RawEvent rawEvent)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("event_id", rawEvent.EventId);
            writer.WriteString("source_id", rawEvent.SourceId);
            writer.WriteString(
                "received_at",
                rawEvent.ReceivedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            writer.WritePropertyName("payload");
            rawEvent.Payload.WriteTo(writer);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
