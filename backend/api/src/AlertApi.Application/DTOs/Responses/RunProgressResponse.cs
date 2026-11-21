using AlertApi.Domain.Models;

namespace AlertApi.Application.DTOs.Responses;

public sealed record RunProgressResponse(
    string RunId,
    string RunbookId,
    string Status,
    int StepsTotal,
    int StepsDone,
    IReadOnlyList<RunProgressStepResponse> Steps)
{
    public static RunProgressResponse From(RunbookRun run, IReadOnlyList<ActionRun> nodes)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(nodes);

        var steps = nodes
            .OrderBy(node => node.StepIndex)
            .Select(node => new RunProgressStepResponse(node.NodeId, node.Status))
            .ToList();

        // A skipped step needs nothing more, so it counts as done: a finished run shows all of its steps done.
        var done = nodes.Count(node => node.Status is ActionRun.Succeeded or ActionRun.Skipped);

        return new RunProgressResponse(run.Id, run.RunbookId, run.Status, nodes.Count, done, steps);
    }
}
