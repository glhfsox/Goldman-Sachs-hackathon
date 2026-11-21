namespace Notifier.Application.DTOs.Config;

public sealed record MembershipDto(
    string TeamId,
    string UserId,
    bool OnCall);
