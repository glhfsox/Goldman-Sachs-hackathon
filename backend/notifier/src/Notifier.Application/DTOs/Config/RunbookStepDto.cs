namespace Notifier.Application.DTOs.Config;

public sealed record RunbookStepDto(
    string Text,
    string? When,
    string? Link);
