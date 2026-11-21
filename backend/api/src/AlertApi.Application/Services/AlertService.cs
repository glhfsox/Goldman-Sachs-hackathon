using AlertApi.Application.DTOs.Responses;
using AlertApi.Application.Exceptions;
using AlertApi.Application.Interfaces;
using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;
using AlertApi.Domain.ValueObjects;

namespace AlertApi.Application.Services;

public sealed class AlertService : IAlertService
{
    private const int MaxLimit = 200;

    private readonly IAlertRepository _alerts;
    private readonly IAlertEventRepository _events;
    private readonly INotificationRepository _notifications;
    private readonly IReferenceDataQueries _queries;
    private readonly IAlertChangeOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public AlertService(
        IAlertRepository alerts,
        IAlertEventRepository events,
        INotificationRepository notifications,
        IReferenceDataQueries queries,
        IAlertChangeOutbox outbox,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _queries = queries;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
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

    public async Task<AlertResponse> AckAsync(string id, string? userId, CancellationToken cancellationToken = default)
    {
        var user = await RequireUserAsync(userId, cancellationToken);
        var alert = await GetRequiredAsync(id, cancellationToken);

        // The processor applies an ack only to an open alert, so the api refuses anything else up front.
        if (alert.Status != Alert.Open)
        {
            throw new AlertStateConflictException(id, alert.Status, "acked");
        }

        return await PublishAsync(alert.ToSnapshot() with { Status = Alert.Acked, AckedBy = user }, "acked", user, cancellationToken);
    }

    public async Task<AlertResponse> ResolveAsync(string id, string? userId, CancellationToken cancellationToken = default)
    {
        var user = await RequireUserAsync(userId, cancellationToken);
        var alert = await GetRequiredAsync(id, cancellationToken);

        if (alert.Status == Alert.Resolved)
        {
            throw new AlertStateConflictException(id, alert.Status, "resolved");
        }

        return await PublishAsync(alert.ToSnapshot() with { Status = Alert.Resolved, ResolvedBy = user }, "resolved", user, cancellationToken);
    }

    private async Task<Alert> GetRequiredAsync(string id, CancellationToken cancellationToken)
    {
        return await _alerts.GetByIdAsync(id, cancellationToken)
            ?? throw new AlertNotFoundException(id);
    }

    private async Task<string> RequireUserAsync(string? userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new InvalidUserException([new ProblemItem("X-User", "header is required")]);
        }

        if (!await _queries.UserExistsAsync(userId, cancellationToken))
        {
            throw new UserNotFoundException(userId);
        }

        return userId;
    }

    private async Task<AlertResponse> PublishAsync(
        AlertSnapshot snapshot,
        string change,
        string userId,
        CancellationToken cancellationToken)
    {
        var response = AlertResponse.From(snapshot);

        // A new change_id per request: the processor and the api copy skip a record they have seen.
        _outbox.Add(Guid.NewGuid().ToString(), change, _timeProvider.GetUtcNow(), userId, response);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return response;
    }
}
