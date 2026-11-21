namespace AlertApi.Application.Runs;

// How a run of a given risk starts (runbook spec §2).
public enum RunStartMode
{
    Immediately,
    AfterCancelWindow,
    AfterApproval
}
