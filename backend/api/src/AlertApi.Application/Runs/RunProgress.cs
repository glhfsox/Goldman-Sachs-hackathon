using System.Text.Json;

using AlertApi.Application.Runbooks;
using AlertApi.Domain.Models;

namespace AlertApi.Application.Runs;

// Applies the planner to a stored run: the rows move, and the run takes the planner's verdict.
public static class RunProgress
{
    // Returns the run's new status when this call ended it (done or failed), otherwise null.
    public static string? Apply(RunbookRun run, IReadOnlyList<ActionRun> nodes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(nodes);

        var plan = RunPlanner.Plan(nodes.Select(ToPlanNode).ToList(), cancelled: false, now);

        Move(nodes, plan, now);

        var ended = plan.Run switch
        {
            RunState.Done => RunbookRun.Done,
            RunState.Failed => RunbookRun.Failed,
            _ => null
        };

        if (ended is null || run.Status == ended)
        {
            return null;
        }

        run.Finish(ended, now);

        return ended;
    }

    // A running node is left to finish; everything not started is cancelled.
    public static void Cancel(RunbookRun run, IReadOnlyList<ActionRun> nodes, string userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(nodes);

        Move(nodes, RunPlanner.Plan(nodes.Select(ToPlanNode).ToList(), cancelled: true, now), now);
        run.Cancel(userId, now);
    }

    private static void Move(IReadOnlyList<ActionRun> nodes, PlanResult plan, DateTimeOffset now)
    {
        var byId = nodes.ToDictionary(node => node.NodeId);

        foreach (var command in plan.Commands)
        {
            byId[command.NodeId].MoveTo(NodeStates.ToName(command.To), command.NotBefore, now);
        }
    }

    private static PlanNode ToPlanNode(ActionRun node)
    {
        var needs = JsonSerializer.Deserialize<List<string>>(node.Needs) ?? [];
        var condition = node.IfStep is not null && node.IfStatus is not null ? new NodeCondition(node.IfStep, node.IfStatus) : null;

        return new PlanNode(node.NodeId, needs, condition, node.IsManual, node.Retries, NodeStates.Parse(node.Status), node.Attempt);
    }
}
