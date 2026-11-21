namespace AlertApi.Application.DTOs.Responses;

// Incident and Runbook stay null until the incident and runbook features exist (contracts/api.md).
public sealed record AlertDetailResponse(
    AlertResponse Alert,
    object? Incident,
    IReadOnlyList<AlertEventResponse> Timeline,
    IReadOnlyList<NotificationResponse> Notifications,
    object? Runbook);
