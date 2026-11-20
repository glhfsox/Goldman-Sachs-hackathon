namespace Notifier.Application.DTOs.Config;

// Preferences (contracts/rules.md §5).
public sealed record PreferencesDto(
    string? Timezone,
    Dictionary<string, List<string>>? BySeverity,
    QuietHoursDto? QuietHours,
    bool WakeMe,
    int? MaxPushPerHour);
