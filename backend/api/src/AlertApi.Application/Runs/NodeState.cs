namespace AlertApi.Application.Runs;

public enum NodeState
{
    Pending,
    Ready,
    Running,
    Succeeded,
    Failed,
    Skipped,
    Blocked,
    Cancelled
}
