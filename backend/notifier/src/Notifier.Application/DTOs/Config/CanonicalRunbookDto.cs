namespace Notifier.Application.DTOs.Config;

// Canonical runbook (contracts/runbooks.md §1): only the step texts reach a message.
public sealed record CanonicalRunbookDto(
    string? Title,
    List<RunbookStepDto>? Steps);
