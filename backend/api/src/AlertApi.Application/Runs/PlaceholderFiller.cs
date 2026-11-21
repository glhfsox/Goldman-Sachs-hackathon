using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlertApi.Application.Runs;

// Fills {{path}} from the alert's sample event with the path rules of contracts/rules.md §1: the first segment is an
// envelope field, otherwise the path is read from `raw`. Filling is one pass and literal: a value that itself contains
// {{...}} is not expanded again, and only strings are filled, never property names.
public static partial class PlaceholderFiller
{
    private static readonly HashSet<string> EnvelopeFields =
        ["source", "type", "severity", "env", "service", "ts", "dedup_hints", "raw"];

    public static FilledParams Fill(JsonElement? parameters, JsonElement envelope)
    {
        if (parameters is not { } value)
        {
            return new FilledParams(null, []);
        }

        var missing = new List<string>();
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream))
        {
            Write(value, envelope, writer, missing);
        }

        using var document = JsonDocument.Parse(stream.ToArray());

        return new FilledParams(document.RootElement.Clone(), missing.Distinct().ToList());
    }

    public static string FillText(string text, JsonElement envelope, List<string> missing)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(missing);

        return PlaceholderPattern().Replace(text, match =>
        {
            var path = match.Groups[1].Value;

            if (TryRender(envelope, path, out var rendered))
            {
                return rendered;
            }

            missing.Add(path);

            return match.Value;
        });
    }

    public static bool TryRender(JsonElement envelope, string path, out string rendered)
    {
        rendered = string.Empty;

        var segments = path.Split('.');
        var current = envelope;

        if (current.ValueKind == JsonValueKind.Object && !EnvelopeFields.Contains(segments[0]))
        {
            if (!current.TryGetProperty("raw", out current))
            {
                return false;
            }
        }

        foreach (var segment in segments)
        {
            if (!TryStep(current, segment, out current))
            {
                return false;
            }
        }

        switch (current.ValueKind)
        {
            case JsonValueKind.String:
                rendered = current.GetString()!;

                return true;
            case JsonValueKind.Number:
                rendered = current.GetRawText();

                return true;
            case JsonValueKind.True:
                rendered = "true";

                return true;
            case JsonValueKind.False:
                rendered = "false";

                return true;
            default:
                return false;
        }
    }

    private static bool TryStep(JsonElement current, string segment, out JsonElement next)
    {
        next = default;

        if (current.ValueKind == JsonValueKind.Object)
        {
            return current.TryGetProperty(segment, out next);
        }

        if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index)
            && index >= 0 && index < current.GetArrayLength())
        {
            next = current[index];

            return true;
        }

        return false;
    }

    private static void Write(JsonElement value, JsonElement envelope, Utf8JsonWriter writer, List<string> missing)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                writer.WriteStringValue(FillText(value.GetString()!, envelope, missing));

                break;
            case JsonValueKind.Object:
                writer.WriteStartObject();

                foreach (var property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    Write(property.Value, envelope, writer, missing);
                }

                writer.WriteEndObject();

                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();

                foreach (var item in value.EnumerateArray())
                {
                    Write(item, envelope, writer, missing);
                }

                writer.WriteEndArray();

                break;
            default:
                value.WriteTo(writer);

                break;
        }
    }

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_][A-Za-z0-9_.\-]*)\s*\}\}")]
    private static partial Regex PlaceholderPattern();
}
