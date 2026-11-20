namespace Notifier.Domain.ValueObjects;

public sealed record NotificationContent
{
    public NotificationContent(
        string title,
        string body,
        string severity,
        string? color,
        IReadOnlyDictionary<string, string> fields,
        string? runbookTitle,
        IReadOnlyList<string> runbookSteps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(severity);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(runbookSteps);

        Title = title;
        Body = body ?? string.Empty;
        Severity = severity;
        Color = color;
        Fields = fields;
        RunbookTitle = runbookTitle;
        RunbookSteps = runbookSteps;
    }

    public string Title { get; }

    public string Body { get; }

    public string Severity { get; }

    public string? Color { get; }

    public IReadOnlyDictionary<string, string> Fields { get; }

    public string? RunbookTitle { get; }

    public IReadOnlyList<string> RunbookSteps { get; }
}
