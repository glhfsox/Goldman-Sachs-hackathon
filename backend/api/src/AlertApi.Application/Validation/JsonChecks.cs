using System.Text.Json;
using System.Text.RegularExpressions;

using AlertApi.Application.Exceptions;

namespace AlertApi.Application.Validation;

// Small helpers for checking a JSON document by hand: the 422 answer must name every wrong path (contracts/api.md).
public static partial class JsonChecks
{
    public static readonly string[] Severities = ["info", "warning", "error", "critical"];

    public static string Join(string parent, string name)
    {
        return parent.Length == 0 ? name : $"{parent}.{name}";
    }

    // JsonElement.TryGetInt32 throws on a non-number, so the kind is checked first.
    public static bool IsWholeNumber(JsonElement value, out int number)
    {
        number = 0;

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number);
    }

    public static bool IsPath(string value)
    {
        return PathPattern().IsMatch(value);
    }

    public static IEnumerable<string> TemplatePaths(string template)
    {
        return TemplatePattern().Matches(template).Select(match => match.Groups[1].Value);
    }

    // The processor rejects unknown fields too, so a typo is reported here instead of failing there.
    public static bool RequireObject(JsonElement element, string path, List<ProblemItem> problems, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            problems.Add(new ProblemItem(path, "must be an object"));

            return false;
        }

        foreach (var property in element.EnumerateObject().Where(property => !allowed.Contains(property.Name)))
        {
            problems.Add(new ProblemItem(Join(path, property.Name), "is not a known field"));
        }

        return true;
    }

    public static bool TryGetRequired(JsonElement obj, string path, string name, List<ProblemItem> problems, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
        {
            return true;
        }

        problems.Add(new ProblemItem(Join(path, name), "is required"));

        return false;
    }

    public static string? CheckString(JsonElement value, string path, List<ProblemItem> problems)
    {
        if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString();
        }

        problems.Add(new ProblemItem(path, "must be a non-empty string"));

        return null;
    }

    public static void CheckOneOf(JsonElement value, string path, List<ProblemItem> problems, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.String || !allowed.Contains(value.GetString()))
        {
            problems.Add(new ProblemItem(path, $"must be one of: {string.Join(", ", allowed)}"));
        }
    }

    public static void CheckBool(JsonElement value, string path, List<ProblemItem> problems)
    {
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            problems.Add(new ProblemItem(path, "must be true or false"));
        }
    }

    public static void CheckPath(JsonElement value, string path, List<ProblemItem> problems)
    {
        if (value.ValueKind != JsonValueKind.String || !IsPath(value.GetString()!))
        {
            problems.Add(new ProblemItem(path, "must be a dot path such as 'raw.error.code'"));
        }
    }

    public static void CheckPathList(JsonElement value, string path, List<ProblemItem> problems)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            problems.Add(new ProblemItem(path, "must be an array of dot paths"));

            return;
        }

        var index = 0;

        foreach (var item in value.EnumerateArray())
        {
            CheckPath(item, $"{path}[{index++}]", problems);
        }
    }

    [GeneratedRegex(@"^[^.]+(\.[^.]+)*$")]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"\{\{\s*(.*?)\s*\}\}")]
    private static partial Regex TemplatePattern();
}
