namespace AlertApi.Application.Runs;

public sealed class RunExecutorOptions
{
    // RUNBOOK_EXECUTION_ENABLED: the kill switch. Off, the workers start but claim nothing.
    public bool Enabled { get; init; } = true;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    // A claim lasts this long; a step's timeout is cut to fit, so a live worker never loses its claim.
    public TimeSpan Lease { get; init; } = TimeSpan.FromMinutes(2);

    public int Concurrency { get; init; } = 4;
}
