using System.Globalization;
using System.Text.Json;

using Ingest.Application.DTOs.Requests;
using Ingest.Domain.Models;
using Ingest.Domain.ValueObjects;

namespace Ingest.Infrastructure.Kafka;

public enum ConfigChangeOutcome
{
    Source,
    Ignored,
    Invalid
}

public sealed record ConfigChangeParseResult(
    ConfigChangeOutcome Outcome,
    ApplySourceChangeRequest? Request,
    string? Reason);

public static class ConfigChangeParser
{
    // The record format of contracts/events.md section 6; ingest copies only kind "source".
    public static ConfigChangeParseResult Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Ignored("empty record");
        }

        try
        {
            using var document = JsonDocument.Parse(value);

            return ParseRoot(document.RootElement);
        }
        catch (JsonException exception)
        {
            return Invalid($"not valid JSON: {exception.Message}");
        }
    }

    private static ConfigChangeParseResult ParseRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Invalid("record is not a JSON object");
        }

        if (GetString(root, "kind") != "source")
        {
            return Ignored("kind is not 'source'");
        }

        // Sources are never deleted, only disabled.
        if (root.TryGetProperty("deleted", out var deleted) && deleted.ValueKind == JsonValueKind.True)
        {
            return Ignored("deleted records are not used for sources");
        }

        if (!DateTimeOffset.TryParse(
                GetString(root, "updated_at"),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var updatedAt))
        {
            return Invalid("updated_at is missing or not a timestamp");
        }

        if (!root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
        {
            return Invalid("body is missing or not an object");
        }

        var id = GetString(body, "id");

        if (string.IsNullOrWhiteSpace(id))
        {
            return Invalid("body.id is missing");
        }

        if (!Enum.TryParse<SourceKind>(GetString(body, "kind"), ignoreCase: true, out var kind)
            || !Enum.IsDefined(kind))
        {
            return Invalid("body.kind must be 'http' or 'kafka'");
        }

        if (!body.TryGetProperty("enabled", out var enabled)
            || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return Invalid("body.enabled must be a boolean");
        }

        var config = ParseConfig(body);

        if (kind == SourceKind.Kafka && string.IsNullOrWhiteSpace(config.Topic))
        {
            return Invalid("a kafka source needs body.config.topic");
        }

        return new ConfigChangeParseResult(
            ConfigChangeOutcome.Source,
            new ApplySourceChangeRequest(id, kind, config, enabled.GetBoolean(), updatedAt),
            Reason: null);
    }

    private static SourceConfig ParseConfig(JsonElement body)
    {
        if (!body.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object)
        {
            return SourceConfig.Empty;
        }

        return new SourceConfig(GetString(config, "topic"), GetString(config, "brokers"));
    }

    private static string? GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static ConfigChangeParseResult Ignored(string reason)
    {
        return new ConfigChangeParseResult(ConfigChangeOutcome.Ignored, Request: null, reason);
    }

    private static ConfigChangeParseResult Invalid(string reason)
    {
        return new ConfigChangeParseResult(ConfigChangeOutcome.Invalid, Request: null, reason);
    }
}
