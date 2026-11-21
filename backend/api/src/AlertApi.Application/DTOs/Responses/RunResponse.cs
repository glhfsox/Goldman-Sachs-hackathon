using AlertApi.Domain.Models;

namespace AlertApi.Application.DTOs.Responses;

public sealed record RunResponse(
    string Id,
    string AlertId,
    string RuleId,
    string TeamId,
    string RunbookId,
    int RunbookVersion,
    string Risk,
    string Trigger,
    string Status,
    DateTimeOffset StartAt,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? CancelledBy,
    RiskCheckResponse? RiskCheck,
    IReadOnlyList<RunNodeResponse> Nodes)
{
    public static RunResponse From(RunbookRun run, IReadOnlyList<ActionRun> nodes)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(nodes);

        return new RunResponse(
            run.Id,
            run.AlertId,
            run.RuleId,
            run.TeamId,
            run.RunbookId,
            run.RunbookVersion,
            run.Risk,
            run.Trigger,
            run.Status,
            run.StartAt,
            run.ApprovedBy,
            run.ApprovedAt,
            run.StartedAt,
            run.FinishedAt,
            run.CancelledBy,
            RiskCheckResponse.From(run),
            nodes.Select(RunNodeResponse.From).ToList());
    }
}
