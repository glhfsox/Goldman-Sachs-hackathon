using System.Text.Json;

namespace AlertApi.Application.Conditions;

// The condition language of contracts/rules.md §2, read exactly like the processor reads it (backend/processor/processor/core.py).
// It never throws: a node the validator would have refused is simply false, so one bad runbook cannot stall the alert feed.
public static class ConditionEvaluator
{
    private static readonly string[] Severities = ["info", "warning", "error", "critical"];

    public static bool Evaluate(JsonElement condition, JsonElement envelope)
    {
        if (condition.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (condition.TryGetProperty("all", out var all))
        {
            return all.ValueKind == JsonValueKind.Array && all.EnumerateArray().All(node => Evaluate(node, envelope));
        }

        if (condition.TryGetProperty("any", out var any))
        {
            return any.ValueKind == JsonValueKind.Array && any.EnumerateArray().Any(node => Evaluate(node, envelope));
        }

        return EvaluateLeaf(condition, envelope);
    }

    private static bool EvaluateLeaf(JsonElement leaf, JsonElement envelope)
    {
        if (!leaf.TryGetProperty("field", out var field) || field.ValueKind != JsonValueKind.String
            || !leaf.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        if (!TryResolve(envelope, field.GetString()!, out var actual))
        {
            return false;
        }

        var operation = op.GetString();

        if (operation == "exists")
        {
            return true;
        }

        if (!leaf.TryGetProperty("value", out var expected))
        {
            return false;
        }

        return operation switch
        {
            "eq" => Same(actual, expected),
            "ne" => !Same(actual, expected),
            "gt" => Compare(actual, expected) is > 0,
            "gte" => Compare(actual, expected) is >= 0,
            "lt" => Compare(actual, expected) is < 0,
            "lte" => Compare(actual, expected) is <= 0,
            "in" => expected.ValueKind == JsonValueKind.Array && expected.EnumerateArray().Any(item => Same(actual, item)),
            "glob" => actual.ValueKind == JsonValueKind.String && expected.ValueKind == JsonValueKind.String
                && Glob(expected.GetString()!, actual.GetString()!),
            "sev_gte" => SeverityAtLeast(actual, expected),
            _ => false
        };
    }

    // Walks objects only, like the processor; a present null resolves.
    private static bool TryResolve(JsonElement envelope, string path, out JsonElement value)
    {
        value = envelope;
        var segments = path.Split('.');

        if (!EnvelopeFields.Names.Contains(segments[0]))
        {
            segments = ["raw", .. segments];
        }

        foreach (var segment in segments)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
            {
                return false;
            }
        }

        return true;
    }

    // JSON equality: true is not 1, 1 equals 1.0, arrays and objects compare element by element.
    private static bool Same(JsonElement left, JsonElement right)
    {
        if (left.ValueKind is JsonValueKind.True or JsonValueKind.False || right.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return left.ValueKind == right.ValueKind;
        }

        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.String:
                return left.GetString() == right.GetString();
            case JsonValueKind.Number:
                return Compare(left, right) == 0;
            case JsonValueKind.Array:
                return left.GetArrayLength() == right.GetArrayLength()
                    && left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => Same(pair.First, pair.Second));
            case JsonValueKind.Object:
                var properties = left.EnumerateObject().ToList();

                return properties.Count == right.EnumerateObject().Count()
                    && properties.All(property => right.TryGetProperty(property.Name, out var other) && Same(property.Value, other));
            default:
                return true;
        }
    }

    // Null when either side is not a number: a comparison with text or a bool is false, never an error.
    private static int? Compare(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != JsonValueKind.Number || right.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (left.TryGetDecimal(out var leftDecimal) && right.TryGetDecimal(out var rightDecimal))
        {
            return leftDecimal.CompareTo(rightDecimal);
        }

        return left.GetDouble().CompareTo(right.GetDouble());
    }

    private static bool SeverityAtLeast(JsonElement actual, JsonElement expected)
    {
        if (actual.ValueKind != JsonValueKind.String || expected.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var wanted = Array.IndexOf(Severities, expected.GetString());

        return wanted >= 0 && Array.IndexOf(Severities, actual.GetString()) >= wanted;
    }

    // Whole-string `*` glob. Chunks are found left to right, so there is no backtracking.
    private static bool Glob(string pattern, string text)
    {
        if (!pattern.Contains('*'))
        {
            return text == pattern;
        }

        var parts = pattern.Split('*');
        var head = parts[0];
        var tail = parts[^1];
        var end = text.Length - tail.Length;

        if (end < head.Length || !text.StartsWith(head, StringComparison.Ordinal) || !text.EndsWith(tail, StringComparison.Ordinal))
        {
            return false;
        }

        var index = head.Length;

        foreach (var chunk in parts[1..^1])
        {
            var found = text.IndexOf(chunk, index, end - index, StringComparison.Ordinal);

            if (found < 0)
            {
                return false;
            }

            index = found + chunk.Length;
        }

        return true;
    }
}
