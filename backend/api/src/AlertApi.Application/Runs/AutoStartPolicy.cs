using AlertApi.Domain.Models;

namespace AlertApi.Application.Runs;

// Whether a run may start without a person, and how (runbook spec §2). Only ever returns the careful answer when unsure.
public static class AutoStartPolicy
{
    // `startMode` is the rule's row in runbook_starts, null when it has none. Approval-risk runs never start by themselves.
    public static RunStartMode? Decide(Risk risk, bool versionAccepted, string? startMode)
    {
        return risk switch
        {
            Risk.ReadOnly => RunStartMode.Immediately,
            Risk.Auto when versionAccepted && startMode == RunbookStart.AutoMode => RunStartMode.AfterCancelWindow,
            _ => null
        };
    }
}
