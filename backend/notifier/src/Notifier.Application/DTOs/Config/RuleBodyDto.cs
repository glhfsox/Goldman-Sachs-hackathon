namespace Notifier.Application.DTOs.Config;

// The parts of a Rule (contracts/rules.md §4) that decide who is told and how.
public sealed record RuleBodyDto(
    string? Id,
    string? TeamId,
    bool? Enabled,
    int? WindowS,
    RouteDto? Route,
    string? RunbookId);
