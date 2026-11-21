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
    private readonly IConfigSnapshotCache _configCache;
    private readonly IDeliverySignal _deliverySignal;
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
        IConfigSnapshotCache configCache,
        IDeliverySignal deliverySignal,
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
        _configCache = configCache;
        _deliverySignal = deliverySignal;
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
        var dueNow = false;
        if (await ApplyToCopyAsync(alert, message, cancellationToken))
        {
            switch (message.Change)
            {
                case AlertChanges.Opened:
                    dueNow = await PlanAsync(alert, message.ChangeId, now, cancellationToken);
                    break;
                case AlertChanges.Reopened:
                case AlertChanges.SeverityRaised:
                    // The new plan replaces the old one; otherwise delayed steps of the old severity would also fire.
                    await CancelOpenAsync(alert.Id, $"superseded by {message.Change}", now, cancellationToken);
                    dueNow = await PlanAsync(alert, message.ChangeId, now, cancellationToken);
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

        // Only after the commit: the delivery loop must be able to claim what it is woken for.
        if (dueNow)
        {
            _deliverySignal.Notify();
        }
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

    // Returns true when at least one planned row is due at once.
    private async Task<bool> PlanAsync(AlertDto alert, string changeId, DateTime now, CancellationToken cancellationToken)
    {
        var config = await _configCache.GetAsync(_config, cancellationToken);
        if (alert.RuleId is null || !config.Rules.TryGetValue(alert.RuleId, out var rule) || !rule.Rule.Enabled)
        {
            _logger.LogWarning("Alert {AlertId} has no enabled rule copy ({RuleId}); nobody is notified", alert.Id, alert.RuleId);

            return false;
        }

        var route = ToRoute(rule.Body?.Route);
        var severity = Severity.ParseOrDefault(alert.Severity);
        var recipients = _recipients.Resolve(rule.Rule.Id, route, severity, config.Memberships, config.Users, config.Preferences);
        if (recipients.Count == 0)
        {
            _logger.LogWarning("Rule {RuleId} routes alert {AlertId} to nobody known yet; nobody is notified", rule.Rule.Id, alert.Id);

            return false;
        }

        var runbook = config.FindActiveRunbook(alert.RunbookId ?? rule.Body?.RunbookId);
        var content = BuildContent(alert, severity, runbook);
        var groupKey = alert.IncidentId ?? ReadEventId(alert) ?? alert.Id;
        var windowSeconds = rule.Body?.WindowS is > 0 ? rule.Body.WindowS.Value : DefaultWindowSeconds;
        var policy = new EscalationPolicy(
            TimeSpan.FromMinutes(_escalation.PushDelayMinutes),
            TimeSpan.FromMinutes(_escalation.OnCallCallDelayMinutes));

        var plan = recipients
            .SelectMany(recipient => _ladder.Plan(recipient, severity, policy, now).Select(planned => (
                Recipient: recipient,
                Planned: planned,
                Id: NotificationId.For(alert.Id, recipient.UserId, planned.Channel.Name, changeId))))
            .ToList();

        // Two queries for the whole plan instead of two per row.
        var existing = await _notifications.GetExistingIdsAsync(plan.Select(entry => entry.Id.Value).ToList(), cancellationToken);
        var alreadyNotified = await _notifications.GetNotifiedUsersAsync(
            recipients.Select(recipient => recipient.UserId).ToList(),
            groupKey,
            alert.Id,
            now.AddSeconds(-windowSeconds),
            cancellationToken);

        var dueNow = false;
        foreach (var (recipient, planned, id) in plan)
        {
            if (existing.Contains(id.Value))
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
            if (alreadyNotified.Contains(recipient.UserId) && planned.Channel != Channel.Ui)
            {
                notification.Suppress("already notified about this incident", now);
                await _timeline.AppendAsync(notification, AlertEventKinds.NotificationSuppressed, now, cancellationToken);
            }
            else if (planned.SendAt <= now)
            {
                dueNow = true;
            }

            await _notifications.AddAsync(notification, cancellationToken);
        }

        return dueNow;
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

    private static NotificationContent BuildContent(AlertDto alert, Severity severity, RunbookCopy? runbook)
    {
        var fields = (alert.Fields ?? [])
            .ToDictionary(field => field.Key, field => FieldText(field.Value));
        var title = string.IsNullOrWhiteSpace(alert.Title) ? $"Alert {alert.RuleId ?? alert.Id}" : alert.Title;

        var steps = new List<string>();
        if (runbook?.Canonical is { } canonical)
        {
            foreach (var step in (canonical.Steps ?? []).Where(step => !string.IsNullOrWhiteSpace(step.Text)))
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
            runbook?.Runbook.Title,
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
