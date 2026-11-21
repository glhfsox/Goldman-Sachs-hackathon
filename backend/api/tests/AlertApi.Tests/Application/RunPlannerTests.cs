using AlertApi.Application.Runbooks;
using AlertApi.Application.Runs;

namespace AlertApi.Tests.Application;

public sealed class RunPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Root_BecomesReady_AndTheRunIsRunning()
    {
        var result = Plan([Node("a")]);

        Assert.Equal([new PlanCommand("a", NodeState.Ready, Now)], result.Commands);
        Assert.Equal(RunState.Running, result.Run);
    }

    [Fact]
    public void Node_WaitsUntilEveryNeedIsFinished()
    {
        var result = Plan([Node("a", state: NodeState.Succeeded), Node("b", state: NodeState.Running), Node("c", "a b")]);

        Assert.Empty(result.Commands);
        Assert.Equal(RunState.Running, result.Run);
    }

    [Fact]
    public void Diamond_JoinsWhenBothBranchesSucceeded()
    {
        var result = Plan([
            Node("a", state: NodeState.Succeeded),
            Node("b", "a", state: NodeState.Succeeded),
            Node("c", "a", state: NodeState.Succeeded),
            Node("d", "b c")]);

        Assert.Equal([new PlanCommand("d", NodeState.Ready, Now)], result.Commands);
    }

    [Fact]
    public void FailedNeed_BlocksTheNodeBehindIt_AndTheRunFails()
    {
        var result = Plan([Node("a", state: NodeState.Failed, attempt: 1), Node("b", "a"), Node("c", "b")]);

        Assert.Equal(
            [new PlanCommand("b", NodeState.Blocked, null), new PlanCommand("c", NodeState.Skipped, null)],
            result.Commands);
        Assert.Equal(RunState.Failed, result.Run);
    }

    [Fact]
    public void IfFailed_RunsTheEscalation_WhenTheWatchedNodeFailedForGood()
    {
        var result = Plan([
            Node("retry", state: NodeState.Failed, attempt: 1),
            Node("escalate", "retry", condition: new NodeCondition("retry", RunbookGraph.Failed))]);

        Assert.Equal([new PlanCommand("escalate", NodeState.Ready, Now)], result.Commands);
        Assert.Equal(RunState.Running, result.Run);
    }

    [Theory]
    [InlineData(NodeState.Succeeded, RunbookGraph.Failed)]
    [InlineData(NodeState.Failed, RunbookGraph.Succeeded)]
    [InlineData(NodeState.Skipped, RunbookGraph.Succeeded)]
    public void IfThatIsFalse_SkipsTheNode(NodeState watched, string status)
    {
        var result = Plan([Node("w", state: watched, attempt: 1), Node("n", "w", condition: new NodeCondition("w", status))]);

        Assert.Equal([new PlanCommand("n", NodeState.Skipped, null)], result.Commands);
    }

    [Fact]
    public void SkippedNeed_WithoutAnIfNamingIt_SkipsTheNode_AndEveryNodeBehindIt()
    {
        var result = Plan([Node("a", state: NodeState.Skipped), Node("b", "a"), Node("c", "b")]);

        Assert.Equal(
            [new PlanCommand("b", NodeState.Skipped, null), new PlanCommand("c", NodeState.Skipped, null)],
            result.Commands);
        Assert.Equal(RunState.Done, result.Run);
    }

    [Fact]
    public void BlockedNeed_FromAMissingPlaceholder_SkipsTheNodeBehindIt_AndTheRunFails()
    {
        var result = Plan([Node("a", state: NodeState.Blocked), Node("b", "a")]);

        Assert.Equal([new PlanCommand("b", NodeState.Skipped, null)], result.Commands);
        Assert.Equal(RunState.Failed, result.Run);
    }

    [Fact]
    public void FailedNode_WithAttemptsLeft_IsReadyAgainAfterABackoffOfTwoSecondsPerAttempt()
    {
        var first = Plan([Node("a", state: NodeState.Failed, retries: 2, attempt: 1), Node("b", "a")]);
        var second = Plan([Node("a", state: NodeState.Failed, retries: 2, attempt: 2), Node("b", "a")]);

        Assert.Equal([new PlanCommand("a", NodeState.Ready, Now.AddSeconds(2))], first.Commands);
        Assert.Equal([new PlanCommand("a", NodeState.Ready, Now.AddSeconds(4))], second.Commands);
        Assert.Equal(RunState.Running, first.Run);
    }

    [Fact]
    public void FailedNode_OutOfAttempts_IsFinal()
    {
        var result = Plan([Node("a", state: NodeState.Failed, retries: 1, attempt: 2)]);

        Assert.Empty(result.Commands);
        Assert.Equal(RunState.Failed, result.Run);
    }

    [Fact]
    public void TextStep_IsNeverMoved_AndKeepsTheRunOpenUntilATickedStepSucceeds()
    {
        var open = Plan([Node("a", state: NodeState.Succeeded), Node("t", manual: true)]);
        var ticked = Plan([Node("a", state: NodeState.Succeeded), Node("t", manual: true, state: NodeState.Succeeded)]);

        Assert.Empty(open.Commands);
        Assert.Equal(RunState.Running, open.Run);
        Assert.Equal(RunState.Done, ticked.Run);
    }

    [Fact]
    public void Cancel_StopsEveryNodeNotStarted_AndLeavesARunningOneAlone()
    {
        var result = Plan(
            [Node("a", state: NodeState.Succeeded), Node("b", state: NodeState.Running), Node("c", "b"), Node("d", state: NodeState.Ready)],
            cancelled: true);

        Assert.Equal(
            [new PlanCommand("c", NodeState.Cancelled, null), new PlanCommand("d", NodeState.Cancelled, null)],
            result.Commands);
        Assert.Equal(RunState.Cancelled, result.Run);
    }

    [Fact]
    public void FailedNodeWithoutAnyFailure_RunIsDone_WhenEveryNodeSucceededOrWasSkipped()
    {
        var result = Plan([Node("a", state: NodeState.Succeeded), Node("b", "a", state: NodeState.Skipped)]);

        Assert.Equal(RunState.Done, result.Run);
    }

    [Fact]
    public void EmptyRun_IsDone()
    {
        Assert.Equal(RunState.Done, Plan([]).Run);
    }

    [Fact]
    public void IfThatNamesAStepOutsideNeeds_BlocksInsteadOfGuessing()
    {
        var result = Plan([Node("a", state: NodeState.Succeeded), Node("b", "a", condition: new NodeCondition("zzz", RunbookGraph.Failed))]);

        Assert.Equal([new PlanCommand("b", NodeState.Blocked, null)], result.Commands);
    }

    private static PlanResult Plan(IReadOnlyList<PlanNode> nodes, bool cancelled = false)
    {
        return RunPlanner.Plan(nodes, cancelled, Now);
    }

    // `needs` is a space-separated list of ids.
    private static PlanNode Node(
        string id,
        string needs = "",
        NodeState state = NodeState.Pending,
        int retries = 0,
        int attempt = 0,
        bool manual = false,
        NodeCondition? condition = null)
    {
        return new PlanNode(id, needs.Split(' ', StringSplitOptions.RemoveEmptyEntries), condition, manual, retries, state, attempt);
    }
}
