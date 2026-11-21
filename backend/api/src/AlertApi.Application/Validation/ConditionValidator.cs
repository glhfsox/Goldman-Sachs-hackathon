using System.Text.Json;

using AlertApi.Application.Exceptions;

namespace AlertApi.Application.Validation;

// The condition language of contracts/rules.md section 2, checked exactly like the processor reads it.
public static class ConditionValidator
{
    private static readonly string[] Operators = ["eq", "ne", "gt", "gte", "lt", "lte", "in", "glob", "sev_gte", "exists"];

    public static void Validate(JsonElement node, string path, List<ProblemItem> problems)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            problems.Add(new ProblemItem(path, "must be an object"));

            return;
        }

        var group = node.TryGetProperty("all", out _) ? "all" : node.TryGetProperty("any", out _) ? "any" : null;

        if (group is null)
        {
            ValidateLeaf(node, path, problems);

            return;
        }

        JsonChecks.RequireObject(node, path, problems, group);
        var groupPath = JsonChecks.Join(path, group);
        var items = node.GetProperty(group);

        if (items.ValueKind != JsonValueKind.Array)
        {
            problems.Add(new ProblemItem(groupPath, "must be an array of conditions"));

            return;
        }

        var index = 0;

        foreach (var item in items.EnumerateArray())
        {
            Validate(item, $"{groupPath}[{index++}]", problems);
        }
    }

    private static void ValidateLeaf(JsonElement leaf, string path, List<ProblemItem> problems)
    {
        JsonChecks.RequireObject(leaf, path, problems, "field", "op", "value");

        if (JsonChecks.TryGetRequired(leaf, path, "field", problems, out var field))
        {
            JsonChecks.CheckPath(field, JsonChecks.Join(path, "field"), problems);
        }

        if (!JsonChecks.TryGetRequired(leaf, path, "op", problems, out var op))
        {
            return;
        }

        var opPath = JsonChecks.Join(path, "op");

        if (op.ValueKind != JsonValueKind.String || !Operators.Contains(op.GetString()))
        {
            problems.Add(new ProblemItem(opPath, $"unknown op '{op}'"));

            return;
        }

        CheckValue(op.GetString()!, leaf, JsonChecks.Join(path, "value"), problems);
    }

    private static void CheckValue(string op, JsonElement leaf, string valuePath, List<ProblemItem> problems)
    {
        var hasValue = leaf.TryGetProperty("value", out var value);

        switch (op)
        {
            case "exists" when hasValue:
                problems.Add(new ProblemItem(valuePath, "op 'exists' takes no value"));
                break;
            case "exists":
                break;
            case "eq" or "ne" when !hasValue:
                problems.Add(new ProblemItem(valuePath, $"op '{op}' needs a value"));
                break;
            case "gt" or "gte" or "lt" or "lte" when !hasValue || value.ValueKind != JsonValueKind.Number:
                problems.Add(new ProblemItem(valuePath, $"op '{op}' needs a numeric value"));
                break;
            case "in" when !hasValue || value.ValueKind != JsonValueKind.Array:
                problems.Add(new ProblemItem(valuePath, "op 'in' needs an array value"));
                break;
            case "glob" when !hasValue || value.ValueKind != JsonValueKind.String:
                problems.Add(new ProblemItem(valuePath, "op 'glob' needs a string value"));
                break;
            case "sev_gte" when !hasValue || value.ValueKind != JsonValueKind.String || !JsonChecks.Severities.Contains(value.GetString()):
                problems.Add(new ProblemItem(valuePath, $"op 'sev_gte' needs a value in [{string.Join(", ", JsonChecks.Severities)}]"));
                break;
        }
    }
}
