using System.Text.Json;

using AlertApi.Application.Exceptions;

namespace AlertApi.Application.Validation;

// A schema mapping as the processor reads it (contracts/rules.md section 3).
public static class MappingValidator
{
    private static readonly string[] FieldNames = ["type", "service", "severity", "env", "ts"];

    public static List<ProblemItem> Validate(JsonElement mapping)
    {
        var problems = new List<ProblemItem>();

        if (!JsonChecks.RequireObject(
                mapping, string.Empty, problems,
                "id", "status", "origin", "priority", "match", "fields", "severity_map", "dedup_fields"))
        {
            return problems;
        }

        if (JsonChecks.TryGetRequired(mapping, string.Empty, "id", problems, out var id))
        {
            JsonChecks.CheckString(id, "id", problems);
        }

        if (JsonChecks.TryGetRequired(mapping, string.Empty, "status", problems, out var status))
        {
            JsonChecks.CheckOneOf(status, "status", problems, "active", "disabled");
        }

        if (JsonChecks.TryGetRequired(mapping, string.Empty, "origin", problems, out var origin))
        {
            JsonChecks.CheckOneOf(origin, "origin", problems, "seed", "human", "llm");
        }

        if (JsonChecks.TryGetRequired(mapping, string.Empty, "priority", problems, out var priority) && !JsonChecks.IsWholeNumber(priority, out _))
        {
            problems.Add(new ProblemItem("priority", "must be a whole number"));
        }

        if (JsonChecks.TryGetRequired(mapping, string.Empty, "match", problems, out var match))
        {
            ConditionValidator.Validate(match, "match", problems);
        }

        if (JsonChecks.TryGetRequired(mapping, string.Empty, "fields", problems, out var fields))
        {
            ValidateFields(fields, problems);
        }

        if (mapping.TryGetProperty("severity_map", out var severityMap))
        {
            ValidateSeverityMap(severityMap, problems);
        }

        if (mapping.TryGetProperty("dedup_fields", out var dedupFields))
        {
            JsonChecks.CheckPathList(dedupFields, "dedup_fields", problems);
        }

        return problems;
    }

    private static void ValidateFields(JsonElement fields, List<ProblemItem> problems)
    {
        if (!JsonChecks.RequireObject(fields, "fields", problems, FieldNames))
        {
            return;
        }

        foreach (var field in fields.EnumerateObject().Where(field => FieldNames.Contains(field.Name)))
        {
            var path = JsonChecks.Join("fields", field.Name);
            var allowed = field.Name == "ts" ? new[] { "path", "const", "format" } : ["path", "const"];

            if (!JsonChecks.RequireObject(field.Value, path, problems, allowed))
            {
                continue;
            }

            var hasPath = field.Value.TryGetProperty("path", out var source);
            var hasConst = field.Value.TryGetProperty("const", out _);

            if (hasPath == hasConst)
            {
                problems.Add(new ProblemItem(path, "must be {path} or {const}"));
            }
            else if (hasPath)
            {
                JsonChecks.CheckPath(source, JsonChecks.Join(path, "path"), problems);
            }

            if (field.Value.TryGetProperty("format", out var format))
            {
                JsonChecks.CheckOneOf(format, JsonChecks.Join(path, "format"), problems, "iso", "epoch_ms", "epoch_s");
            }
        }
    }

    private static void ValidateSeverityMap(JsonElement severityMap, List<ProblemItem> problems)
    {
        if (!JsonChecks.RequireObject(severityMap, "severity_map", problems, [.. severityMap.ValueKind == JsonValueKind.Object ? severityMap.EnumerateObject().Select(entry => entry.Name) : []]))
        {
            return;
        }

        foreach (var entry in severityMap.EnumerateObject())
        {
            JsonChecks.CheckOneOf(entry.Value, JsonChecks.Join("severity_map", entry.Name), problems, JsonChecks.Severities);
        }
    }
}
