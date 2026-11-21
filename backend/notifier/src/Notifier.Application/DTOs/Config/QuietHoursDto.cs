namespace Notifier.Application.DTOs.Config;

public sealed record QuietHoursDto(
    string Start,
    string End,
    Dictionary<string, string>? Downgrade);
