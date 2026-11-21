using AlertApi.Application.Runbooks;
using AlertApi.Application.Runs;

using FsCheck;
using FsCheck.Fluent;

namespace AlertApi.Tests.Application;

// Random DAGs run to the end against a fake executor whose outcomes come from the generated data.
public sealed class RunPlannerPropertyTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    public sealed record Spec(
        int[] NeedMask,
        int[] IfKind,
        int[] Retries,
        bool[] Outcomes);

    private static readonly Gen<Spec> SpecGen =
        from count in Gen.Choose(1, 12)
        from needMask in Gen.Choose(0, 255).ArrayOf(count)
        from ifKind in Gen.Choose(0, 2).ArrayOf(count)
        from retries in Gen.Choose(0, 3).ArrayOf(count)
        from outcomes in Gen.Elements(true, false).ArrayOf(count * 4)
        select new Spec(needMask, ifKind, retries, outcomes);

    [Fact]
    public void EveryRandomGraph_Terminates_RunsEachNodeAtMostOncePlusRetries_AndNeverBeforeItsNeeds()
    {
        Prop.ForAll(SpecGen.ToArbitrary(), spec =>
        {
            var run = Simulate(spec);

            return run.Finished
                && run.Calls.All(pair => pair.Value <= 1 + spec.Retries[pair.Key])
                && !run.OrderViolated;
        }).QuickCheckThrowOnFailure();
    }

    [Fact]
    public void SameInput_GivesTheSameCommandsAndRunState()
    {
        Prop.ForAll(SpecGen.ToArbitrary(), spec =>
        {
            var nodes = Build(spec).Select(node => node).ToList();
            var first = RunPlanner.Plan(nodes, cancelled: false, Start);
            var second = RunPlanner.Plan(nodes, cancelled: false, Start);

            return first.Run == second.Run && first.Commands.SequenceEqual(second.Commands);
        }).QuickCheckThrowOnFailure();
    }

    [Fact]
    public void FinishedRun_HasNoNodeLeftPending_ReadyOrRunning()
    {
        Prop.ForAll(SpecGen.ToArbitrary(), spec =>
        {
            var run = Simulate(spec);

            return run.Finished && run.Final.All(state => state is not (NodeState.Pending or NodeState.Ready or NodeState.Running))
                && run.RunState is RunState.Done or RunState.Failed;
        }).QuickCheckThrowOnFailure();
    }

    private sealed record Outcome(
        bool Finished,
        bool OrderViolated,
        Dictionary<int, int> Calls,
        NodeState[] Final,
        RunState RunState);

    private static List<PlanNode> Build(Spec spec)
    {
        var nodes = new List<PlanNode>();

        for (var index = 0; index < spec.NeedMask.Length; index++)
        {
            // Needs point only to earlier nodes, so the graph is acyclic by construction.
            var needs = Enumerable.Range(0, Math.Min(index, 8)).Where(earlier => (spec.NeedMask[index] >> earlier & 1) == 1)
                .Select(earlier => $"n{earlier}").ToList();
            NodeCondition? condition = spec.IfKind[index] == 0 || needs.Count == 0
                ? null
                : new NodeCondition(needs[0], spec.IfKind[index] == 1 ? RunbookGraph.Succeeded : RunbookGraph.Failed);

            nodes.Add(new PlanNode($"n{index}", needs, condition, IsManual: false, spec.Retries[index], NodeState.Pending, Attempt: 0));
        }

        return nodes;
    }

    private static Outcome Simulate(Spec spec)
    {
        var nodes = Build(spec);
        var calls = new Dictionary<int, int>();
        var violated = false;
        var plan = RunPlanner.Plan(nodes, cancelled: false, Start);

        for (var step = 0; step < 1000; step++)
        {
            nodes = Apply(nodes, plan);

            var ready = nodes.FindIndex(node => node.State == NodeState.Ready);

            if (ready < 0)
            {
                return new Outcome(true, violated, calls, nodes.Select(node => node.State).ToArray(), plan.Run);
            }

            var node = nodes[ready];
            var attempt = node.Attempt;

            violated |= node.Needs.Any(need => nodes.First(candidate => candidate.Id == need).State
                is NodeState.Pending or NodeState.Ready or NodeState.Running);
            calls[ready] = calls.GetValueOrDefault(ready) + 1;

            var succeeded = spec.Outcomes[ready * 4 + attempt];

            nodes[ready] = node with { State = succeeded ? NodeState.Succeeded : NodeState.Failed, Attempt = attempt + 1 };
            plan = RunPlanner.Plan(nodes, cancelled: false, Start);
        }

        return new Outcome(false, violated, calls, nodes.Select(node => node.State).ToArray(), plan.Run);
    }

    private static List<PlanNode> Apply(List<PlanNode> nodes, PlanResult plan)
    {
        var result = nodes.ToList();

        foreach (var command in plan.Commands)
        {
            var index = result.FindIndex(node => node.Id == command.NodeId);

            result[index] = result[index] with { State = command.To };
        }

        return result;
    }
}
