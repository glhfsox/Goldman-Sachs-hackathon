namespace Notifier.Domain.Models;

public class ProcessedChange
{
    private ProcessedChange()
    {
    }

    public string ChangeId { get; private set; } = null!;

    public DateTime ProcessedAt { get; private set; }

    public static ProcessedChange Create(string changeId, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(changeId);

        return new ProcessedChange
        {
            ChangeId = changeId,
            ProcessedAt = now
        };
    }
}
