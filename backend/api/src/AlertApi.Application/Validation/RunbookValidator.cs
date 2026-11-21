using System.Text.Json;
using System.Text.RegularExpressions;

using AlertApi.Application.Exceptions;
using AlertApi.Application.Runbooks;

namespace AlertApi.Application.Validation;

// The canonical runbook of contracts/runbooks.md §1, extended with the graph fields of the runbook spec (#73):
// every problem is reported at once with its path, so the ai-worker can hand them back to the model.
public static partial class RunbookValidator
{
    public const int MaxSteps = 50;

    private static readonly string[] CanonicalFields = ["id", "title", "matches", "steps"];

    // `approval` is accepted for older senders and ignored: the action's risk decides how a run starts.
    private static readonly string[] StepFields =
        ["id", "text", "action", "params", "approval", "when", "link", "needs", "if", "retries", "timeout_s"];

    public static List<ProblemItem> ValidateCreate(string? teamId, string? title, string? format, string? source)
    {
        var problems = new List<ProblemItem>();

        RequireText(teamId, "team_id", problems);
        RequireText(title, "title", problems);
        RequireText(format, "format", problems);
        RequireText(source, "source", problems);

        return problems;
    }

    public static List<ProblemItem> ValidateCanonical(JsonElement canonical, string runbookId)
    {
        var problems = new List<ProblemItem>();

        if (!JsonChecks.RequireObject(canonical, "canonical", problems, CanonicalFields))
        {
            return problems;
        }

        if (JsonChecks.TryGetRequired(canonical, "canonical", "id", problems, out var id)
            && JsonChecks.CheckString(id, "canonical.id", problems) is { } idText && idText != runbookId)
        {
            problems.Add(new ProblemItem("canonical.id", "must equal the runbook id"));
        }

        if (JsonChecks.TryGetRequired(canonical, "canonical", "title", problems, out var title))
        {
            JsonChecks.CheckString(title, "canonical.title", problems);
        }

        if (canonical.TryGetProperty("matches", out var matches))
        {
            ConditionValidator.Validate(matches, "canonical.matches", problems);
        }

        if (!JsonChecks.TryGetRequired(canonical, "canonical", "steps", problems, out var steps))
        {
            return problems;
        }

        if (steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() == 0)
        {
            problems.Add(new ProblemItem("canonical.steps", "must be a non-empty array"));

            return problems;
        }

        if (steps.GetArrayLength() > MaxSteps)
        {
            problems.Add(new ProblemItem("canonical.steps", $"has more than {MaxSteps} steps"));

            return problems;
        }

        var index = 0;

        foreach (var step in steps.EnumerateArray())
        {
            ValidateStep(step, $"canonical.steps[{index++}]", problems);
        }

        if (problems.Count == 0)
        {
            ValidateGraph(RunbookGraph.Parse(canonical), problems);
        }

        return problems;
    }

    public static List<ProblemItem> ValidateStatus(string status)
    {
        return status is Domain.Models.Runbook.Draft or Domain.Models.Runbook.Active
            ? []
            : [new ProblemItem("status", "must be one of: draft, active")];
    }

    private static void ValidateStep(JsonElement step, string path, List<ProblemItem> problems)
    {
        if (!JsonChecks.RequireObject(step, path, problems, StepFields))
        {
            return;
        }

        if (JsonChecks.TryGetRequired(step, path, "text", problems, out var text))
        {
            JsonChecks.CheckString(text, JsonChecks.Join(path, "text"), problems);
        }

        if (step.TryGetProperty("id", out var id)
            && (id.ValueKind != JsonValueKind.String || !NodeIdPattern().IsMatch(id.GetString()!)))
        {
            problems.Add(new ProblemItem(JsonChecks.Join(path, "id"), "must match [a-z0-9_-]{1,40}"));
        }

        foreach (var name in new[] { "action", "when" })
        {
            if (step.TryGetProperty(name, out var value))
            {
                JsonChecks.CheckString(value, JsonChecks.Join(path, name), problems);
            }
        }

        if (step.TryGetProperty("params", out var parameters) && parameters.ValueKind != JsonValueKind.Object)
        {
            problems.Add(new ProblemItem(JsonChecks.Join(path, "params"), "must be an object"));
        }

        if (step.TryGetProperty("params", out _) && !step.TryGetProperty("action", out _))
        {
            problems.Add(new ProblemItem(JsonChecks.Join(path, "params"), "needs an action"));
        }

        if (step.TryGetProperty("approval", out var approval))
        {
            JsonChecks.CheckOneOf(approval, JsonChecks.Join(path, "approval"), problems, "none", "required");
        }

        if (step.TryGetProperty("link", out var link)
            && (link.ValueKind != JsonValueKind.String
                || !Uri.TryCreate(link.GetString(), UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https")))
        {
            problems.Add(new ProblemItem(JsonChecks.Join(path, "link"), "must be an http:// or https:// URL"));
        }

        if (step.TryGetProperty("needs", out var needs)
            && (needs.ValueKind != JsonValueKind.Array || needs.EnumerateArray().Any(need => need.ValueKind != JsonValueKind.String)))
        {
            problems.Add(new ProblemItem(JsonChecks.Join(path, "needs"), "must be an array of step ids"));
        }

        if (step.TryGetProperty("if", out var condition))
        {
            ValidateCondition(condition, JsonChecks.Join(path, "if"), problems);
        }

        CheckRange(step, "retries", 0, 3, path, problems);
        CheckRange(step, "timeout_s", 1, 300, path, problems);
    }

    private static void ValidateCondition(JsonElement condition, string path, List<ProblemItem> problems)
    {
        if (!JsonChecks.RequireObject(condition, path, problems, "step", "status"))
        {
            return;
        }

        if (JsonChecks.TryGetRequired(condition, path, "step", problems, out var step))
        {
            JsonChecks.CheckString(step, JsonChecks.Join(path, "step"), problems);
        }

        if (JsonChecks.TryGetRequired(condition, path, "status", problems, out var status))
        {
            JsonChecks.CheckOneOf(status, JsonChecks.Join(path, "status"), problems, RunbookGraph.Succeeded, RunbookGraph.Failed);
        }
    }

    private static void ValidateGraph(IReadOnlyList<RunbookNode> nodes, List<ProblemItem> problems)
    {
        var ids = new HashSet<string>();

        foreach (var node in nodes)
        {
            var path = $"canonical.steps[{node.Index}]";

            if (!ids.Add(node.Id))
            {
                problems.Add(new ProblemItem(JsonChecks.Join(path, "id"), $"duplicate step id '{node.Id}'"));
            }

            if (node.If is { } condition && !node.Needs.Contains(condition.Step))
            {
                problems.Add(new ProblemItem(JsonChecks.Join(path, "if.step"), $"'{condition.Step}' must be one of this step's needs"));
            }
        }

        foreach (var node in nodes)
        {
            foreach (var need in node.Needs.Where(need => !ids.Contains(need) || need == node.Id))
            {
                problems.Add(new ProblemItem($"canonical.steps[{node.Index}].needs", $"'{need}' is not another step's id"));
            }
        }

        if (problems.Count == 0 && !RunbookGraph.IsAcyclic(nodes))
        {
            problems.Add(new ProblemItem("canonical.steps", "the needs form a cycle"));
        }
    }

    private static void CheckRange(JsonElement step, string name, int min, int max, string path, List<ProblemItem> problems)
    {
        if (step.TryGetProperty(name, out var value)
            && (!JsonChecks.IsWholeNumber(value, out var number) || number < min || number > max))
        {
            problems.Add(new ProblemItem(JsonChecks.Join(path, name), $"must be a whole number from {min} to {max}"));
        }
    }

    private static void RequireText(string? value, string path, List<ProblemItem> problems)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add(new ProblemItem(path, "must be a non-empty string"));
        }
    }

    [GeneratedRegex("^[a-z0-9_-]{1,40}$")]
    private static partial Regex NodeIdPattern();
}
