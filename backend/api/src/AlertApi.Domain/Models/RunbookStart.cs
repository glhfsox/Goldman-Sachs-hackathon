namespace AlertApi.Domain.Models;

// How runs of one runbook on one rule start. No row means `approval`: the careful default.
public class RunbookStart
{
    public const string AutoMode = "auto";
    public const string ApprovalMode = "approval";

    private RunbookStart()
    {
    }

    public string RuleId { get; private set; } = null!;

    public string RunbookId { get; private set; } = null!;

    public string Start { get; private set; } = null!;

    public string SetBy { get; private set; } = null!;

    public DateTimeOffset UpdatedAt { get; private set; }

    public static RunbookStart Create(string ruleId, string runbookId, string start, string setBy, DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runbookId);

        var row = new RunbookStart { RuleId = ruleId, RunbookId = runbookId };
        row.Set(start, setBy, updatedAt);

        return row;
    }

    public void Set(string start, string setBy, DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setBy);

        if (start is not (AutoMode or ApprovalMode))
        {
            throw new ArgumentException($"Start mode '{start}' is not 'auto' or 'approval'.", nameof(start));
        }

        Start = start;
        SetBy = setBy;
        UpdatedAt = updatedAt;
    }
}
