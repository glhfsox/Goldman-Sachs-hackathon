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
    private readonly IRunRepository _runs;
    private readonly IReferenceDataQueries _queries;
    private readonly IAlertChangeOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly IRunAutomation _automation;

    public AlertService(
        IAlertRepository alerts,
        IAlertEventRepository events,
        INotificationRepository notifications,
        IRunRepository runs,
        IReferenceDataQueries queries,
        IAlertChangeOutbox outbox,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        IRunAutomation automation)
    {
        _automation = automation;
        _queries = queries;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _alerts = alerts;
        _events = events;
        _notifications = notifications;
        _runs = runs;
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
        var run = await _runs.GetLatestByAlertIdAsync(id, cancellationToken);
        var progress = run is null
            ? null
            : RunProgressResponse.From(run, await _runs.GetNodesAsync(run.Id, cancellationToken));

        return new AlertDetailResponse(
            AlertResponse.From(alert),
            Incident: null,
            timeline.Select(AlertEventResponse.From).ToList(),
            notifications.Select(NotificationResponse.From).ToList(),
            Runbook: progress);
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

        // Same save as the outbox row: nothing starts on an alert a person has just resolved.
        await _automation.StageCancelAsync(id, user, cancellationToken);

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
