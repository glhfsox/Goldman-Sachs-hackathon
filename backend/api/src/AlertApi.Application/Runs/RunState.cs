namespace AlertApi.Application.Runs;

// What the planner derives from the nodes. `scheduled` is set by run creation and never by the planner.
public enum RunState
{
    Running,
    Done,
    Failed,
    Cancelled
}
