using AlertApi.Domain.Models;

namespace AlertApi.Application.Runs;

// The planner works on NodeState; rows store the name.
public static class NodeStates
{
    public static string ToName(NodeState state)
    {
        return state switch
        {
            NodeState.Pending => ActionRun.Pending,
            NodeState.Ready => ActionRun.Ready,
            NodeState.Running => ActionRun.Running,
            NodeState.Succeeded => ActionRun.Succeeded,
            NodeState.Failed => ActionRun.Failed,
            NodeState.Skipped => ActionRun.Skipped,
            NodeState.Blocked => ActionRun.Blocked,
            _ => ActionRun.Cancelled
        };
    }

    public static NodeState Parse(string name)
    {
        return name switch
        {
            ActionRun.Ready => NodeState.Ready,
            ActionRun.Running => NodeState.Running,
            ActionRun.Succeeded => NodeState.Succeeded,
            ActionRun.Failed => NodeState.Failed,
            ActionRun.Skipped => NodeState.Skipped,
            ActionRun.Blocked => NodeState.Blocked,
            ActionRun.Cancelled => NodeState.Cancelled,
            _ => NodeState.Pending
        };
    }
}
