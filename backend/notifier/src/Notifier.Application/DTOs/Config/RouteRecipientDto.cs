namespace Notifier.Application.DTOs.Config;

public sealed record RouteRecipientDto(
    string? Team,
    string? User,
    bool OnCall);
