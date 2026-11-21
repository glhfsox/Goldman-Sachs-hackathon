using AlertApi.Application.Runbooks;

namespace AlertApi.Application.Runs;

// Pure function of the nodes: no clock, no I/O, so every rule of the runbook spec §5 is a unit test. It repeats until
// nothing changes, so skipping one node can skip the nodes behind it in the same call.
public static class RunPlanner
{
    public static readonly TimeSpan BackoffStep = TimeSpan.FromSeconds(2);

    public static PlanResult Plan(IReadOnlyList<PlanNode> nodes, bool cancelled, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var byId = nodes.ToDictionary(node => node.Id);
        var states = nodes.ToDictionary(node => node.Id, node => node.State);
        var commands = new List<PlanCommand>();

        if (cancelled)
        {
            // A node that is running finishes and keeps its result; everything not started is cancelled.
            foreach (var node in nodes.Where(node => IsNotStarted(node, states[node.Id])))
            {
                commands.Add(new PlanCommand(node.Id, NodeState.Cancelled, null));
            }

            return new PlanResult(commands, RunState.Cancelled);
        }

        var changed = true;

        while (changed)
        {
            changed = false;

            foreach (var node in nodes)
            {
                var next = Next(node, byId, states, now);

                if (next is null)
                {
                    continue;
                }

                states[node.Id] = next.To;
                commands.Add(next);
                changed = true;
            }
        }

        return new PlanResult(commands, RunOf(nodes, states));
    }

    private static PlanCommand? Next(
        PlanNode node,
        Dictionary<string, PlanNode> byId,
        Dictionary<string, NodeState> states,
        DateTimeOffset now)
    {
        var state = states[node.Id];

        if (state == NodeState.Failed && node.Attempt < 1 + node.Retries)
        {
            return new PlanCommand(node.Id, NodeState.Ready, now + BackoffStep * node.Attempt);
        }

        // A text-only step is ticked by a person (POST /runs/{id}/nodes/{node}/done); the planner never moves it.
        if (state != NodeState.Pending || node.IsManual)
        {
            return null;
        }

        if (node.Needs.Any(need => !IsTerminal(need, byId, states)))
        {
            return null;
        }

        var to = Decide(node, states);

        return new PlanCommand(node.Id, to, to == NodeState.Ready ? now : null);
    }

    private static NodeState Decide(PlanNode node, Dictionary<string, NodeState> states)
    {
        var others = node.Needs.AsEnumerable();

        if (node.If is { } condition)
        {
            if (!node.Needs.Contains(condition.Step) || !states.TryGetValue(condition.Step, out var watched))
            {
                return NodeState.Blocked;
            }

            var expected = condition.Status == RunbookGraph.Failed ? NodeState.Failed : NodeState.Succeeded;

            if (watched != expected)
            {
                return NodeState.Skipped;
            }

            others = others.Where(need => need != condition.Step);
        }

        var results = others.Select(need => states.GetValueOrDefault(need, NodeState.Blocked)).ToList();

        if (results.Contains(NodeState.Failed))
        {
            return NodeState.Blocked;
        }

        return results.Any(result => result != NodeState.Succeeded) ? NodeState.Skipped : NodeState.Ready;
    }

    // A failed node with attempts left is not final: the planner is about to make it ready again.
    private static bool IsTerminal(string id, Dictionary<string, PlanNode> byId, Dictionary<string, NodeState> states)
    {
        if (!states.TryGetValue(id, out var state))
        {
            return true;
        }

        return state is NodeState.Succeeded or NodeState.Skipped or NodeState.Blocked or NodeState.Cancelled
            || state == NodeState.Failed && byId[id].Attempt >= 1 + byId[id].Retries;
    }

    private static bool IsNotStarted(PlanNode node, NodeState state)
    {
        return state is NodeState.Pending or NodeState.Ready
            || state == NodeState.Failed && node.Attempt < 1 + node.Retries;
    }

    private static RunState RunOf(IReadOnlyList<PlanNode> nodes, Dictionary<string, NodeState> states)
    {
        if (states.Values.Any(state => state is NodeState.Ready or NodeState.Running))
        {
            return RunState.Running;
        }

        if (states.Values.Any(state => state is NodeState.Failed or NodeState.Blocked))
        {
            return RunState.Failed;
        }

        // Pending is left only on text steps a person has not ticked yet.
        return nodes.Count > 0 && states.Values.Any(state => state == NodeState.Pending) ? RunState.Running : RunState.Done;
    }
}
