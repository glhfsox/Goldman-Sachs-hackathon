namespace AlertApi.Application.DTOs.Responses;

// Incident stays null until incidents exist; Runbook is the latest run of the alert, null when none was started (contracts/api.md).
public sealed record AlertDetailResponse(
    AlertResponse Alert,
    object? Incident,
    IReadOnlyList<AlertEventResponse> Timeline,
    IReadOnlyList<NotificationResponse> Notifications,
    RunProgressResponse? Runbook);
