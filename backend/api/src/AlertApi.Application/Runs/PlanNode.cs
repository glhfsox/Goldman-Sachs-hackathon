using AlertApi.Application.Runbooks;

namespace AlertApi.Application.Runs;

// A node as the planner sees it. Needs are the effective ones (RunbookGraph.EffectiveNeeds); Attempt counts the calls
// already made, so a node may run again while Attempt < 1 + Retries.
public sealed record PlanNode(
    string Id,
    IReadOnlyList<string> Needs,
    NodeCondition? If,
    bool IsManual,
    int Retries,
    NodeState State,
    int Attempt);
