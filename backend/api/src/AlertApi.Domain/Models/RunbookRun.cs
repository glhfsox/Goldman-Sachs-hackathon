namespace AlertApi.Domain.Models;

// One run of a runbook on an alert (runbook spec #73). The nodes are ActionRun rows; the planner decides their states
// and this aggregate only records the run's own status.
public class RunbookRun
{
    public const string Scheduled = "scheduled";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public const string Manual = "manual";

    private RunbookRun()
    {
    }

    public string Id { get; private set; } = null!;

    public string AlertId { get; private set; } = null!;

    public string RuleId { get; private set; } = null!;

    public string TeamId { get; private set; } = null!;

    public string RunbookId { get; private set; } = null!;

    public int RunbookVersion { get; private set; }

    public string Risk { get; private set; } = null!;

    public string Trigger { get; private set; } = null!;

    public string Status { get; private set; } = null!;

    public DateTimeOffset StartAt { get; private set; }

    public string? ApprovedBy { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public string? CancelledBy { get; private set; }

    public bool IsLive => Status is Scheduled or Running;

    // A person who starts a run by hand approves exactly what the preview showed, whatever the risk.
    public static RunbookRun StartByHand(
        string id,
        string alertId,
        string ruleId,
        string teamId,
        string runbookId,
        int runbookVersion,
        string risk,
        string userId,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(alertId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runbookId);
        ArgumentException.ThrowIfNullOrWhiteSpace(risk);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        return new RunbookRun
        {
            Id = id,
            AlertId = alertId,
            RuleId = ruleId,
            TeamId = teamId,
            RunbookId = runbookId,
            RunbookVersion = runbookVersion,
            Risk = risk,
            Trigger = Manual,
            Status = Running,
            StartAt = now,
            ApprovedBy = userId,
            ApprovedAt = now,
            CreatedAt = now,
            StartedAt = now
        };
    }

    // Done and failed are set by the planner's verdict; the run stays open until then.
    public void Finish(string status, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        Status = status;
        FinishedAt = at;
    }

    public void Cancel(string userId, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        CancelledBy = userId;
        Finish(Cancelled, at);
    }
}
