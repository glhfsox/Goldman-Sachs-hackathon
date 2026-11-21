using AlertApi.Application.DTOs.Responses;
using AlertApi.Application.Exceptions;
using AlertApi.Application.Interfaces;
using AlertApi.Domain.Repositories;

namespace AlertApi.Application.Services;

public sealed class AlertService : IAlertService
{
    private const int MaxLimit = 200;

    private readonly IAlertRepository _alerts;
    private readonly IAlertEventRepository _events;
    private readonly INotificationRepository _notifications;

    public AlertService(
        IAlertRepository alerts,
        IAlertEventRepository events,
        INotificationRepository notifications)
    {
        _alerts = alerts;
        _events = events;
        _notifications = notifications;
    }

    public async Task<IReadOnlyList<AlertResponse>> GetAllAsync(
        string? teamId,
        string? status,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var alerts = await _alerts.GetAllAsync(teamId, status, Math.Clamp(limit, 1, MaxLimit), cancellationToken);

        return alerts.Select(AlertResponse.From).ToList();
    }

    public async Task<AlertDetailResponse> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var alert = await _alerts.GetByIdAsync(id, cancellationToken)
            ?? throw new AlertNotFoundException(id);
        var timeline = await _events.GetByAlertIdAsync(id, cancellationToken);
        var notifications = await _notifications.GetByAlertIdAsync(id, cancellationToken);

        return new AlertDetailResponse(
            AlertResponse.From(alert),
            Incident: null,
            timeline.Select(AlertEventResponse.From).ToList(),
            notifications.Select(NotificationResponse.From).ToList(),
            Runbook: null);
    }
}
