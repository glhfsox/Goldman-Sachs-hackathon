namespace AlertApi.Application.Runs;

// A state change the caller stores. NotBefore is set for Ready only: when the node may be claimed.
public sealed record PlanCommand(
    string NodeId,
    NodeState To,
    DateTimeOffset? NotBefore);
