namespace AlertApi.Application.Runs;

public sealed record PlanResult(
    IReadOnlyList<PlanCommand> Commands,
    RunState Run);
