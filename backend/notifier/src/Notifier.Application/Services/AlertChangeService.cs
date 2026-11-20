using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Notifier.Application.Constants;
using Notifier.Application.DTOs.Config;
using Notifier.Application.DTOs.Messages;
using Notifier.Application.Exceptions;
using Notifier.Application.Interfaces;
using Notifier.Application.Options;
using Notifier.Application.Serialization;
using Notifier.Application.Templates;
using Notifier.Domain.Models;
using Notifier.Domain.Repositories;
using Notifier.Domain.Services;
using Notifier.Domain.ValueObjects;

namespace Notifier.Application.Services;

public sealed class AlertChangeService : IAlertChangeService
{
    private const int DefaultWindowSeconds = 300;

    private readonly INotificationRepository _notifications;
    private readonly IAlertRepository _alerts;
    private readonly IConfigRepository _config;
    private readonly IProcessedChangeRepository _processedChanges;
    private readonly ITimelineService _timeline;
    private readonly EscalationLadder _ladder;
    private readonly RecipientResolver _recipients;
    private readonly IClock _clock;
    private readonly EscalationOptions _escalation;
    private readonly ILogger<AlertChangeService> _logger;

    public AlertChangeService(
        INotificationRepository notifications,
        IAlertRepository alerts,
        IConfigRepository config,
        IProcessedChangeRepository processedChanges,
        ITimelineService timeline,
        EscalationLadder ladder,
        RecipientResolver recipients,
        IClock clock,
        IOptions<EscalationOptions> escalation,
        ILogger<AlertChangeService> logger)
    {
        _notifications = notifications;
        _alerts = alerts;
        _config = config;
        _processedChanges = processedChanges;
        _timeline = timeline;
        _ladder = ladder;
        _recipients = recipients;
        _clock = clock;
        _escalation = escalation.Value;
        _logger = logger;
    }

    public async Task HandleAsync(AlertChangeMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (await _processedChanges.ExistsAsync(message.ChangeId, cancellationToken))
        {
            _logger.LogDebug("Alert change {ChangeId} was already processed; skipping the redelivery", message.ChangeId);

            return;
        }

        var alert = ReadAlert(message);
        var now = _clock.UtcNow;
        if (await ApplyToCopyAsync(alert, message, cancellationToken))
        {
            switch (message.Change)
            {
                case AlertChanges.Opened:
                    await PlanAsync(alert, message.ChangeId, now, cancellationToken);
                    break;
                case AlertChanges.Reopened:
                case AlertChanges.SeverityRaised:
                    // The new plan replaces the old one; otherwise delayed steps of the old severity would also fire.
                    await CancelOpenAsync(alert.Id, $"superseded by {message.Change}", now, cancellationToken);
                    await PlanAsync(alert, message.ChangeId, now, cancellationToken);
                    break;
                case AlertChanges.Acked:
                case AlertChanges.Resolved:
                    await CancelOpenAsync(alert.Id, message.Change, now, cancellationToken);
                    break;
                case AlertChanges.CountThreshold:
                    // Counter thresholds only move the UI counter; they never disturb anyone.
                    break;
                default:
                    _logger.LogWarning("Alert change {ChangeId} has unknown kind {Change}; ignoring it", message.ChangeId, message.Change);
                    break;
            }
        }
        else
        {
            _logger.LogInformation("Alert change {ChangeId} is older than the alert copy; ignoring it", message.ChangeId);
        }

        await _processedChanges.AddAsync(ProcessedChange.Create(message.ChangeId, now), cancellationToken);
        await _processedChanges.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> ApplyToCopyAsync(AlertDto alert, AlertChangeMessage message, CancellationToken cancellationToken)
    {
        var status = Enum.TryParse<AlertStatus>(alert.Status, ignoreCase: true, out var parsed) ? parsed : AlertStatus.Open;
        var severity = Severity.ParseOrDefault(alert.Severity).Name;
        var changedAt = Time.ToUtc(message.At);
        var body = message.Alert.GetRawText();

        var copy = await _alerts.FindAsync(alert.Id, cancellationToken);
        if (copy is null)
        {
            await _alerts.AddAsync(Alert.Create(alert.Id, alert.RuleId, alert.IncidentId, status, severity, body, changedAt), cancellationToken);

            return true;
        }

        return copy.Apply(status, severity, alert.IncidentId, body, changedAt);
    }

    private async Task PlanAsync(AlertDto alert, string changeId, DateTime now, CancellationToken cancellationToken)
    {
        var rule = alert.RuleId is null ? null : await _config.FindRuleAsync(alert.RuleId, cancellationToken);
        if (rule is null || !rule.Enabled)
        {
            _logger.LogWarning("Alert {AlertId} has no enabled rule copy ({RuleId}); nobody is notified", alert.Id, alert.RuleId);

            return;
        }

        var ruleBody = JsonSerializer.Deserialize<RuleBodyDto>(rule.Body, NotifierJson.Options);
        var route = ToRoute(ruleBody?.Route);
        var severity = Severity.ParseOrDefault(alert.Severity);
        var recipients = await ResolveRecipientsAsync(rule.Id, route, severity, cancellationToken);
        if (recipients.Count == 0)
        {
            _logger.LogWarning("Rule {RuleId} routes alert {AlertId} to nobody known yet; nobody is notified", rule.Id, alert.Id);

            return;
        }

        var runbook = await FindActiveRunbookAsync(alert.RunbookId ?? ruleBody?.RunbookId, cancellationToken);
        var content = BuildContent(alert, severity, runbook);
        var groupKey = alert.IncidentId ?? ReadEventId(alert) ?? alert.Id;
        var windowSeconds = ruleBody?.WindowS is > 0 ? ruleBody.WindowS.Value : DefaultWindowSeconds;
        var policy = new EscalationPolicy(
            TimeSpan.FromMinutes(_escalation.PushDelayMinutes),
            TimeSpan.FromMinutes(_escalation.OnCallCallDelayMinutes));

        foreach (var recipient in recipients)
        {
            var alreadyNotified = await _notifications.HasDeliveredToGroupAsync(
                recipient.UserId,
                groupKey,
                alert.Id,
                now.AddSeconds(-windowSeconds),
                cancellationToken);

            foreach (var planned in _ladder.Plan(recipient, severity, policy, now))
            {
                var id = NotificationId.For(alert.Id, recipient.UserId, planned.Channel.Name, changeId);
                if (await _notifications.ExistsAsync(id.Value, cancellationToken))
                {
                    continue;
                }

                var notification = Notification.Create(
                    id,
                    alert.Id,
                    alert.IncidentId,
                    groupKey,
                    recipient,
                    planned.Channel,
                    changeId,
                    planned.Step,
                    $"{recipient.Why}; {planned.Reason}",
                    content,
                    planned.SendAt,
                    now);

                // One message per person per incident (AC-5.3); the UI card is never noise.
                if (alreadyNotified && planned.Channel != Channel.Ui)
                {
                    notification.Suppress("already notified about this incident", now);
                    await _timeline.AppendAsync(notification, AlertEventKinds.NotificationSuppressed, now, cancellationToken);
                }

                await _notifications.AddAsync(notification, cancellationToken);
            }
        }
    }

    private async Task<IReadOnlyList<Recipient>> ResolveRecipientsAsync(
        string ruleId,
        RouteSpec route,
        Severity severity,
        CancellationToken cancellationToken)
    {
        var teamIds = route.Recipients.Where(spec => spec.TeamId is not null).Select(spec => spec.TeamId!).ToList();
        var namedUserIds = route.Recipients.Where(spec => spec.UserId is not null).Select(spec => spec.UserId!).ToList();
        var memberships = await _config.GetMembershipsAsync(teamIds, namedUserIds, cancellationToken);

        var userIds = memberships.Select(membership => membership.UserId).Concat(namedUserIds).Distinct().ToList();
        var users = (await _config.GetUsersAsync(userIds, cancellationToken)).ToDictionary(user => user.Id);
        var preferences = (await _config.GetPreferencesAsync(userIds, cancellationToken))
            .ToDictionary(preference => preference.UserId, preference => ToSettings(preference.Body));

        return _recipients.Resolve(ruleId, route, severity, memberships, users, preferences);
    }

    private async Task<Runbook?> FindActiveRunbookAsync(string? runbookId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runbookId))
        {
            return null;
        }

        var runbook = await _config.FindRunbookAsync(runbookId, cancellationToken);

        return runbook is { IsActive: true } ? runbook : null;
    }

    private async Task CancelOpenAsync(string alertId, string reason, DateTime now, CancellationToken cancellationToken)
    {
        foreach (var notification in await _notifications.GetOpenByAlertAsync(alertId, cancellationToken))
        {
            notification.Cancel($"suppressed by {reason}", now);
            await _timeline.AppendAsync(notification, AlertEventKinds.NotificationSuppressed, now, cancellationToken);
        }
    }

    private static AlertDto ReadAlert(AlertChangeMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.ChangeId))
        {
            throw new InvalidAlertChangeException("(none)", "change_id is missing");
        }

        if (message.Alert.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidAlertChangeException(message.ChangeId, "alert is not an object");
        }

        var alert = message.Alert.Deserialize<AlertDto>(NotifierJson.Options);
        if (alert is null || string.IsNullOrWhiteSpace(alert.Id))
        {
            throw new InvalidAlertChangeException(message.ChangeId, "alert.id is missing");
        }

        return alert;
    }

    // An invalid entry is skipped rather than failing the alert: the api validates rules, this is a safety net.
    private static RouteSpec ToRoute(RouteDto? route)
    {
        var recipients = (route?.Recipients ?? [])
            .Where(entry => string.IsNullOrWhiteSpace(entry.Team) != string.IsNullOrWhiteSpace(entry.User))
            .Select(entry => new RecipientSpec(entry.Team, entry.User, entry.OnCall))
            .ToList();

        return new RouteSpec(recipients, route?.Channels);
    }

    private static PreferenceSettings ToSettings(string body)
    {
        var dto = JsonSerializer.Deserialize<PreferencesDto>(body, NotifierJson.Options);
        QuietHours? quietHours = null;
        if (dto?.QuietHours is { } quiet
            && TimeOnly.TryParse(quiet.Start, out var start)
            && TimeOnly.TryParse(quiet.End, out var end))
        {
            quietHours = new QuietHours(start, end, quiet.Downgrade ?? []);
        }

        var bySeverity = (dto?.BySeverity ?? [])
            .ToDictionary(entry => entry.Key.ToLowerInvariant(), entry => (IReadOnlyList<string>)entry.Value);

        return new PreferenceSettings(dto?.Timezone, bySeverity, quietHours, dto?.WakeMe ?? false, dto?.MaxPushPerHour);
    }

    private static NotificationContent BuildContent(AlertDto alert, Severity severity, Runbook? runbook)
    {
        var fields = (alert.Fields ?? [])
            .ToDictionary(field => field.Key, field => FieldText(field.Value));
        var title = string.IsNullOrWhiteSpace(alert.Title) ? $"Alert {alert.RuleId ?? alert.Id}" : alert.Title;

        var steps = new List<string>();
        if (runbook?.Canonical is not null)
        {
            var canonical = JsonSerializer.Deserialize<CanonicalRunbookDto>(runbook.Canonical, NotifierJson.Options);
            foreach (var step in (canonical?.Steps ?? []).Where(step => !string.IsNullOrWhiteSpace(step.Text)))
            {
                var text = TemplateRenderer.Fill(step.Text, alert.SampleEvent);
                if (step.When is not null)
                {
                    text = $"{text} (when: {step.When})";
                }

                if (step.Link is not null)
                {
                    text = $"{text}: {step.Link}";
                }

                steps.Add(text);
            }
        }

        return new NotificationContent(
            title,
            alert.Body ?? string.Empty,
            severity.Name,
            alert.Color,
            fields,
            runbook?.Title,
            steps);
    }

    private static string? ReadEventId(AlertDto alert)
    {
        if (alert.SampleEvent is { ValueKind: JsonValueKind.Object } sample
            && sample.TryGetProperty("event_id", out var eventId)
            && eventId.ValueKind == JsonValueKind.String)
        {
            return eventId.GetString();
        }

        return null;
    }

    private static string FieldText(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
}
