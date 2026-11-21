namespace AlertApi.Application.Runs;

public sealed class RunAutomationOptions
{
    // RUNBOOK_CANCEL_WINDOW_S: how long an `auto` run waits so a person can still cancel it.
    public TimeSpan CancelWindow { get; init; } = TimeSpan.FromSeconds(30);

    // An alert change older than this starts nothing: a rebuilt api database replays old records, and
    // a late start would act on a problem that is long over.
    public TimeSpan MaxChangeAge { get; init; } = TimeSpan.FromMinutes(5);

    // RUNBOOK_RISK_CHECK: when true an `auto` run starts only after the ai-worker cleared it; no verdict by
    // its start time means a person decides. When false the run starts at its start time unchecked.
    public bool RiskCheck { get; init; } = true;
}
