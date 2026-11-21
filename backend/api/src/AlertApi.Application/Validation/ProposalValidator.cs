using System.Text.Json;

using AlertApi.Application.Actions;
using AlertApi.Application.Exceptions;

namespace AlertApi.Application.Validation;

// The proposal bodies of contracts/api.md. Extra fields (stats, reasons, probabilities) are kept as sent: only what
// accepting a proposal reads is checked, and every path starts with "body." like the request.
public static class ProposalValidator
{
    public const string Threshold = "threshold";
    public const string Template = "template";
    public const string Runbook = "runbook";
    public const string RuleSeverity = "rule_severity";
    public const string RunbookStart = "runbook_start";

    public static readonly string[] Kinds = [Threshold, Template, Runbook, RuleSeverity, RunbookStart];

    private const int MaxBodyBytes = 64 * 1024;

    public static List<ProblemItem> Validate(string? kind, JsonElement? body, ActionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var problems = new List<ProblemItem>();

        if (string.IsNullOrWhiteSpace(kind) || !Kinds.Contains(kind))
        {
            problems.Add(new ProblemItem("kind", $"must be one of {string.Join(", ", Kinds)}"));
        }

        if (body is not { ValueKind: JsonValueKind.Object } value)
        {
            problems.Add(new ProblemItem("body", "must be an object"));

            return problems;
        }

        if (value.GetRawText().Length > MaxBodyBytes)
        {
            problems.Add(new ProblemItem("body", $"must be at most {MaxBodyBytes} characters"));

            return problems;
        }

        var bodyProblems = new List<ProblemItem>();

        switch (kind)
        {
            case Runbook:
                RequireStrings(value, bodyProblems, "team_id", "title");
                ValidateCanonical(value, catalog, bodyProblems);
                break;
            case RuleSeverity:
                RequireStrings(value, bodyProblems, "rule_id", "team_id");
                RequireOneOf(value, bodyProblems, "severity", JsonChecks.Severities);
                break;
            case RunbookStart:
                RequireStrings(value, bodyProblems, "rule_id", "team_id", "runbook_id");
                RequireOneOf(value, bodyProblems, "start", ["auto", "approval"]);
                break;
        }

        problems.AddRange(bodyProblems.Select(problem => new ProblemItem(JsonChecks.Join("body", problem.Path), problem.Message)));

        return problems;
    }

    private static void RequireStrings(JsonElement body, List<ProblemItem> problems, params string[] names)
    {
        foreach (var name in names)
        {
            if (JsonChecks.TryGetRequired(body, string.Empty, name, problems, out var value))
            {
                JsonChecks.CheckString(value, name, problems);
            }
        }
    }

    private static void RequireOneOf(JsonElement body, List<ProblemItem> problems, string name, string[] allowed)
    {
        if (JsonChecks.TryGetRequired(body, string.Empty, name, problems, out var value))
        {
            JsonChecks.CheckOneOf(value, name, problems, allowed);
        }
    }

    private static void ValidateCanonical(JsonElement body, ActionCatalog catalog, List<ProblemItem> problems)
    {
        if (!JsonChecks.TryGetRequired(body, string.Empty, "canonical", problems, out var canonical))
        {
            return;
        }

        // The runbook gets its own id when it is saved, so the canonical's id only has to be a string here.
        var id = canonical.ValueKind == JsonValueKind.Object && canonical.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()!
            : string.Empty;

        problems.AddRange(RunbookValidator.ValidateCanonical(canonical, id, catalog));
    }
}
