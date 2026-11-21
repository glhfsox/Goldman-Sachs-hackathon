using System.Text;
using System.Text.Json;

namespace Ingest.Domain.Services;

public static class PayloadParser
{
    // Valid JSON stays JSON; anything else (a log line, broken JSON) becomes a string.
    // Invalid UTF-8 bytes become U+FFFD so that one bad byte never loses the event.
    public static JsonElement Parse(ReadOnlySpan<byte> body)
    {
        var text = Encoding.UTF8.GetString(body);

        try
        {
            using var document = JsonDocument.Parse(text);

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(text);
        }
    }
}
