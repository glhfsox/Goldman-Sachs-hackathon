namespace AlertApi.Domain.Models;

// One node of a run: a runbook step with its params filled, the request frozen when the run was created, and the
// graph fields the planner needs. A row changes state only through RunPlanner commands, the executor and a person's tick.
public class ActionRun
{
    public const string Pending = "pending";
    public const string Ready = "ready";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
    public const string Blocked = "blocked";
    public const string Cancelled = "cancelled";

    private ActionRun()
    {
    }

    public string Id { get; private set; } = null!;

    public string RunId { get; private set; } = null!;

    public string AlertId { get; private set; } = null!;

    public string RunbookId { get; private set; } = null!;

    public string NodeId { get; private set; } = null!;

    public int StepIndex { get; private set; }

    public string Text { get; private set; } = null!;

    // Null for a text-only step, which a person ticks.
    public string? Action { get; private set; }

    public string? Params { get; private set; }

    public string Risk { get; private set; } = null!;

    public string? TargetId { get; private set; }

    public int? TargetVersion { get; private set; }

    // The request a plugin sends, resolved at run creation so a later edit of a target changes only later runs.
    public string? Request { get; private set; }

    public string Needs { get; private set; } = null!;

    public string? IfStep { get; private set; }

    public string? IfStatus { get; private set; }

    public int Retries { get; private set; }

    public int? TimeoutSeconds { get; private set; }

    public string Status { get; private set; } = null!;

    public int Attempt { get; private set; }

    public DateTimeOffset? NotBefore { get; private set; }

    public DateTimeOffset? LeaseUntil { get; private set; }

    public string IdempotencyKey { get; private set; } = null!;

    public string? Result { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public bool IsManual => Action is null;

    public static ActionRun Create(
        string runId,
        string alertId,
        string runbookId,
        string nodeId,
        int stepIndex,
        string text,
        string? action,
        string? parameters,
        string risk,
        string? targetId,
        int? targetVersion,
        string? request,
        string needs,
        string? ifStep,
        string? ifStatus,
        int retries,
        int? timeoutSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(alertId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runbookId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(risk);
        ArgumentException.ThrowIfNullOrWhiteSpace(needs);

        var key = $"{runId}:{nodeId}";

        return new ActionRun
        {
            Id = key,
            RunId = runId,
            AlertId = alertId,
            RunbookId = runbookId,
            NodeId = nodeId,
            StepIndex = stepIndex,
            Text = text ?? string.Empty,
            Action = action,
            Params = parameters,
            Risk = risk,
            TargetId = targetId,
            TargetVersion = targetVersion,
            Request = request,
            Needs = needs,
            IfStep = ifStep,
            IfStatus = ifStatus,
            Retries = retries,
            TimeoutSeconds = timeoutSeconds,
            Status = Pending,
            IdempotencyKey = key
        };
    }

    public void MoveTo(string status, DateTimeOffset? notBefore, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        Status = status;
        NotBefore = notBefore;

        if (status is Skipped or Blocked or Cancelled)
        {
            FinishedAt = at;
        }
    }

    // The executor's verdict on the call it claimed; the lease ends with it.
    public void Finish(bool ok, string result, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(result);

        if (Status != Running)
        {
            throw new InvalidOperationException($"Node '{NodeId}' is {Status}, not running.");
        }

        Status = ok ? Succeeded : Failed;
        Result = result;
        FinishedAt = at;
        LeaseUntil = null;
    }

    // A person ticked a text-only step.
    public void Tick(DateTimeOffset at)
    {
        if (!IsManual || Status != Pending)
        {
            throw new InvalidOperationException($"Node '{NodeId}' cannot be ticked.");
        }

        Status = Succeeded;
        FinishedAt = at;
    }
}
