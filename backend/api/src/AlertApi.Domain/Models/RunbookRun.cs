namespace AlertApi.Domain.Models;

// One run of a runbook on an alert (runbook spec #73). The nodes are ActionRun rows; the planner decides their states
// and this aggregate only records the run's own status.
public class RunbookRun
{
    public const string Scheduled = "scheduled";
    public const string WaitingApproval = "waiting_approval";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public const string Manual = "manual";
    public const string Auto = "auto";

    public const string Clear = "clear";
    public const string Hold = "hold";

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

    // The alert change that started this run; unique, so a record Kafka delivers twice starts one run.
    public string? TriggerChangeId { get; private set; }

    // The risk check's answer (step 5); verdict is null until it answers. A run nobody answered for is held by the scheduler.
    public string? RiskCheckVerdict { get; private set; }

    public double? RiskCheckConfidence { get; private set; }

    // A JSON array of plain sentences for the UI.
    public string? RiskCheckReasons { get; private set; }

    public string? RiskCheckBy { get; private set; }

    public DateTimeOffset? RiskCheckAt { get; private set; }

    public bool IsLive => Status is Scheduled or WaitingApproval or Running;

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

    // `read_only` runs start at once; an `auto` run waits for its cancel window (`startAt` later than `now`).
    // Nobody approved it: the risk check of step 5 and the rule's start mode stand in for a person.
    public static RunbookRun StartAutomatically(
        string id,
        string alertId,
        string ruleId,
        string teamId,
        string runbookId,
        int runbookVersion,
        string risk,
        string triggerChangeId,
        DateTimeOffset startAt,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(alertId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runbookId);
        ArgumentException.ThrowIfNullOrWhiteSpace(risk);
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerChangeId);

        var immediate = startAt <= now;

        return new RunbookRun
        {
            Id = id,
            AlertId = alertId,
            RuleId = ruleId,
            TeamId = teamId,
            RunbookId = runbookId,
            RunbookVersion = runbookVersion,
            Risk = risk,
            Trigger = Auto,
            Status = immediate ? Running : Scheduled,
            StartAt = startAt,
            CreatedAt = now,
            StartedAt = immediate ? now : null,
            TriggerChangeId = triggerChangeId
        };
    }

    // `clear` lets the run start at its start time; `hold` sends it to a person at once, even over an earlier `clear`.
    public void RecordRiskCheck(string verdict, double? confidence, string reasonsJson, string by, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verdict);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonsJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(by);

        if (Status != Scheduled)
        {
            throw new InvalidOperationException($"Run '{Id}' is {Status}; only a scheduled run takes a risk check.");
        }

        RiskCheckVerdict = verdict;
        RiskCheckConfidence = confidence;
        RiskCheckReasons = reasonsJson;
        RiskCheckBy = by;
        RiskCheckAt = at;

        if (verdict == Hold)
        {
            Status = WaitingApproval;
        }
    }

    // A person approves a held run; the executor then takes its steps.
    public void Approve(string userId, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        if (Status != WaitingApproval)
        {
            throw new InvalidOperationException($"Run '{Id}' is {Status}; only a held run is approved.");
        }

        Status = Running;
        ApprovedBy = userId;
        ApprovedAt = at;
        StartedAt = at;
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
