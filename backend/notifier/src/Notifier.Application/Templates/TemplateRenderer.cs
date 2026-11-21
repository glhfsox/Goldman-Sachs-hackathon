using System.Text.Json;
using System.Text.RegularExpressions;

namespace Notifier.Application.Templates;

// Fills {{path}} placeholders from the alert's sample_event, with the path rules of contracts/rules.md §1:
// a first segment that is not an envelope field is resolved against raw; a missing path renders as "".
public static partial class TemplateRenderer
{
    private static readonly HashSet<string> EnvelopeFields =
    [
        "event_id", "source", "type", "severity", "env", "service", "ts", "dedup_hints",
        "mapped", "mapping_id", "received_at", "raw"
    ];

    public static string Fill(string template, JsonElement? envelope)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        return Placeholder().Replace(template, match => Resolve(envelope, match.Groups[1].Value));
    }

    private static string Resolve(JsonElement? envelope, string path)
    {
        if (envelope is not { ValueKind: JsonValueKind.Object } root)
        {
            return string.Empty;
        }

        var segments = path.Split('.');
        var current = root;
        if (!EnvelopeFields.Contains(segments[0]))
        {
            if (!root.TryGetProperty("raw", out current))
            {
                return string.Empty;
            }
        }

        foreach (var segment in segments)
        {
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out var child))
            {
                current = child;
            }
            else if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index)
                && index >= 0 && index < current.GetArrayLength())
            {
                current = current[index];
            }
            else
            {
                return string.Empty;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => current.GetRawText()
        };
    }

    [GeneratedRegex(@"\{\{\s*([^{}\s]+)\s*\}\}")]
    private static partial Regex Placeholder();
}
