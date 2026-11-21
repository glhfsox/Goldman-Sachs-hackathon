using AlertApi.Domain.Models;

namespace AlertApi.Application.Interfaces;

// Runs that start and stop without a person asking (contracts/runbooks.md §2).
public interface IRunAutomation
{
    // Stages a run for a newly opened or reopened alert; the caller saves. Never throws for a runbook that cannot run:
    // that is logged and skipped, because a consumer retries a failing record forever.
    Task StageStartAsync(Alert alert, string changeId, DateTimeOffset changeAt, CancellationToken cancellationToken = default);

    // Stages the cancel of the alert's live run, if any; the caller saves. Safe to repeat.
    Task StageCancelAsync(string alertId, string actor, CancellationToken cancellationToken = default);

    // Starts one scheduled run whose cancel window ended. False when none is due.
    Task<bool> StartDueAsync(CancellationToken cancellationToken = default);
}
