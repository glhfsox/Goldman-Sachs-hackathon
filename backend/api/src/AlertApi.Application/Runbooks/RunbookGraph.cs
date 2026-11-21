using System.Text.Json;

namespace AlertApi.Application.Runbooks;

public sealed record NodeCondition(
    string Step,
    string Status);

public sealed record RunbookNode(
    string Id,
    int Index,
    string Text,
    string? Action,
    JsonElement? Params,
    IReadOnlyList<string> Needs,
    bool NeedsDeclared,
    NodeCondition? If,
    int Retries,
    int? TimeoutSeconds);

// The steps of a canonical runbook as a graph. Old linear runbooks have no ids or needs: a step without an id is
// "step-<index>", and a step without needs waits for the step before it (contracts/runbooks.md §1).
public static class RunbookGraph
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";

    public static string DefaultId(int index)
    {
        return $"step-{index}";
    }

    // Reads a canonical runbook that RunbookValidator accepted; anything it would reject is skipped, not thrown.
    public static IReadOnlyList<RunbookNode> Parse(JsonElement canonical)
    {
        var nodes = new List<RunbookNode>();

        if (!canonical.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
        {
            return nodes;
        }

        var index = 0;
        string? previous = null;

        foreach (var step in steps.EnumerateArray())
        {
            var id = StringOf(step, "id") ?? DefaultId(index);
            var declared = step.TryGetProperty("needs", out var needsElement) && needsElement.ValueKind == JsonValueKind.Array;
            var needs = declared
                ? needsElement.EnumerateArray().Where(need => need.ValueKind == JsonValueKind.String).Select(need => need.GetString()!).ToList()
                : previous is null ? [] : [previous];

            nodes.Add(new RunbookNode(
                id,
                index,
                StringOf(step, "text") ?? string.Empty,
                StringOf(step, "action"),
                step.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object ? parameters.Clone() : null,
                needs,
                declared,
                ConditionOf(step),
                IntOf(step, "retries") ?? 0,
                IntOf(step, "timeout_s")));

            previous = id;
            index++;
        }

        return nodes;
    }

    // What each node really waits for. A step that only has text is done by a person, and a step that waits for it by
    // default (no `needs` written) must not stall the run: it waits for what that text step waited for. A `needs`
    // that names a text step is kept, so the author can still make a step wait for a person (runbook spec §3).
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> EffectiveNeeds(IReadOnlyList<RunbookNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var byId = nodes.GroupBy(node => node.Id).ToDictionary(group => group.Key, group => group.First());
        var result = new Dictionary<string, IReadOnlyList<string>>();

        foreach (var node in nodes)
        {
            result[node.Id] = node.NeedsDeclared ? node.Needs : Through(node.Needs, byId, []);
        }

        return result;
    }

    private static List<string> Through(IReadOnlyList<string> needs, Dictionary<string, RunbookNode> byId, HashSet<string> seen)
    {
        var result = new List<string>();

        foreach (var need in needs)
        {
            if (byId.TryGetValue(need, out var target) && target.Action is null && seen.Add(need))
            {
                result.AddRange(target.NeedsDeclared ? target.Needs : Through(target.Needs, byId, seen));
            }
            else
            {
                result.Add(need);
            }
        }

        return result.Distinct().ToList();
    }

    // Kahn's algorithm: true when every node can be ordered after the nodes it needs.
    public static bool IsAcyclic(IReadOnlyList<RunbookNode> nodes)
    {
        var known = nodes.Select(node => node.Id).ToHashSet();
        var waiting = nodes.ToDictionary(node => node.Id, node => node.Needs.Count(known.Contains));
        var ready = new Queue<string>(waiting.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var ordered = 0;

        while (ready.Count > 0)
        {
            var done = ready.Dequeue();
            ordered++;

            foreach (var node in nodes.Where(node => node.Needs.Contains(done)))
            {
                if (--waiting[node.Id] == 0)
                {
                    ready.Enqueue(node.Id);
                }
            }
        }

        return ordered == waiting.Count;
    }

    private static string? StringOf(JsonElement step, string name)
    {
        return step.ValueKind == JsonValueKind.Object && step.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int? IntOf(JsonElement step, string name)
    {
        return step.ValueKind == JsonValueKind.Object && step.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
    }

    private static NodeCondition? ConditionOf(JsonElement step)
    {
        if (step.ValueKind != JsonValueKind.Object || !step.TryGetProperty("if", out var condition)
            || condition.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var target = StringOf(condition, "step");
        var status = StringOf(condition, "status");

        return target is null || status is null ? null : new NodeCondition(target, status);
    }
}
