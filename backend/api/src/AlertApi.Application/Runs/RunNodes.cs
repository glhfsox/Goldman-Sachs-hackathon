using System.Text.Json;

using AlertApi.Domain.Models;

namespace AlertApi.Application.Runs;

// Builds the stored rows of a run from its draft; manual and automatic start share it.
public static class RunNodes
{
    public static List<ActionRun> Create(RunbookRun run, RunDraft draft, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(draft);

        var nodes = draft.Nodes.Select(node => ToRow(run, node)).ToList();

        // A step the alert has no value for never runs with an empty one: it starts blocked.
        foreach (var node in nodes.Where((_, index) => draft.Nodes[index].Missing.Count > 0))
        {
            node.MoveTo(ActionRun.Blocked, null, now);
        }

        return nodes;
    }

    private static ActionRun ToRow(RunbookRun run, DraftNode node)
    {
        var step = node.Step;

        return ActionRun.Create(
            run.Id,
            run.AlertId,
            run.RunbookId,
            step.Id,
            step.Index,
            node.Text,
            step.Action,
            node.Params?.GetRawText(),
            node.Risk,
            node.TargetId,
            node.TargetVersion,
            node.Request,
            JsonSerializer.Serialize(node.Needs),
            step.If?.Step,
            step.If?.Status,
            step.Retries,
            step.TimeoutSeconds);
    }
}
