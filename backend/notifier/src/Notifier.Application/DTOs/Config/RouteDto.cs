namespace Notifier.Application.DTOs.Config;

public sealed record RouteDto(
    List<RouteRecipientDto>? Recipients,
    List<string>? Channels);
