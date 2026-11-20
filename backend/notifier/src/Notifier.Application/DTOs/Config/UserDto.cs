namespace Notifier.Application.DTOs.Config;

public sealed record UserDto(
    string Id,
    string? Name,
    string? Email,
    string? Phone);
