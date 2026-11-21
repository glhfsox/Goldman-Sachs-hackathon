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

        await HandleBatchAsync([message], cancellationToken);
    }

    // Every lookup is one query for the whole batch, and the batch commits once: the cost per record is
    // in-memory work, not round trips. Records are still applied one after another, in consumption order.
    public async Task HandleBatchAsync(IReadOnlyList<AlertChangeMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var changes = await ReadNewChangesAsync(messages, cancellationToken);
        if (changes.Count == 0)
        {
            return;
        }

        var now = _clock.UtcNow;
        var config = await _configCache.GetAsync(_config, cancellationToken);
        var copies = (await _alerts.GetByIdsAsync(changes.Select(change => change.Alert.Id).Distinct().ToList(), cancellationToken))
            .ToDictionary(copy => copy.Id);

        // Plans are pure: drafted for every change that may plan, used only if the change turns out newer than the copy.
        var drafts = changes
            .Where(change => change.Message.Change is AlertChanges.Opened or AlertChanges.Reopened or AlertChanges.SeverityRaised)
            .ToDictionary(change => change.Message.ChangeId, change => Draft(change.Alert, change.Message.ChangeId, config, now));
        var plans = drafts.Values.OfType<PlanDraft>().ToList();
        var groupKeys = plans.Select(plan => plan.GroupKey).Distinct().ToList();
        var batch = new BatchState();

        if (plans.Count > 0)
        {
            // Held until the save at the end: the "already notified" read below then sees every committed plan of these groups.
            await _notifications.LockGroupsAsync(groupKeys, cancellationToken);
            batch.Existing = await _notifications.GetExistingIdsAsync(
                plans.SelectMany(plan => plan.Entries.Select(entry => entry.Id.Value)).ToList(),
                cancellationToken);
            batch.Notified.AddRange(await _notifications.GetNotifiedAsync(
                plans.SelectMany(plan => plan.Entries.Select(entry => entry.Recipient.UserId)).Distinct().ToList(),
                groupKeys,
                now.AddSeconds(-plans.Max(plan => plan.WindowSeconds)),
                cancellationToken));
        }

        var cancelling = changes
            .Where(change => change.Message.Change is AlertChanges.Reopened or AlertChanges.SeverityRaised
                or AlertChanges.Acked or AlertChanges.Resolved)
            .Select(change => change.Alert.Id)
            .Distinct()
            .ToList();
        batch.Open.AddRange(await _notifications.GetOpenByAlertsAsync(cancelling, cancellationToken));

        var dueNow = false;
        foreach (var (message, alert) in changes)
        {
            if (await ApplyToCopyAsync(alert, message, copies, cancellationToken))
            {
                switch (message.Change)
                {
                    case AlertChanges.Opened:
                        dueNow |= await PlanAsync(alert, drafts[message.ChangeId], batch, now, cancellationToken);
                        break;
                    case AlertChanges.Reopened:
                    case AlertChanges.SeverityRaised:
                        // The new plan replaces the old one; otherwise delayed steps of the old severity would also fire.
                        await CancelOpenAsync(alert.Id, $"superseded by {message.Change}", batch, now, cancellationToken);
                        dueNow |= await PlanAsync(alert, drafts[message.ChangeId], batch, now, cancellationToken);
                        break;
                    case AlertChanges.Acked:
                    case AlertChanges.Resolved:
                        await CancelOpenAsync(alert.Id, message.Change, batch, now, cancellationToken);
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
        }

        await _processedChanges.SaveChangesAsync(cancellationToken);

        // Only after the commit: the delivery loop must be able to claim what it is woken for.
        if (dueNow)
        {
            _deliverySignal.Notify();
        }
    }

    // Valid records not seen before, in order: invalid ones are logged and skipped, repeats within the batch
    // and changes already processed (a redelivery) are dropped.
    private async Task<List<(AlertChangeMessage Message, AlertDto Alert)>> ReadNewChangesAsync(
        IReadOnlyList<AlertChangeMessage> messages,
        CancellationToken cancellationToken)
    {
        var changes = new List<(AlertChangeMessage Message, AlertDto Alert)>();
        var seen = new HashSet<string>();
        foreach (var message in messages)
        {
            try
            {
                var alert = ReadAlert(message);
                if (seen.Add(message.ChangeId))
                {
                    changes.Add((message, alert));
                }
            }
            catch (InvalidAlertChangeException exception)
            {
                _logger.LogError(exception, "Skipping an invalid alerts.changes record");
            }
        }

        var processed = await _processedChanges.GetExistingAsync(seen, cancellationToken);
        if (processed.Count > 0)
        {
            _logger.LogDebug("Skipping {Count} alert changes that were already processed (redelivery)", processed.Count);
        }

        return changes.Where(change => !processed.Contains(change.Message.ChangeId)).ToList();
    }

    private async Task<bool> ApplyToCopyAsync(
        AlertDto alert,
        AlertChangeMessage message,
        Dictionary<string, Alert> copies,
        CancellationToken cancellationToken)
    {
        var status = Enum.TryParse<AlertStatus>(alert.Status, ignoreCase: true, out var parsed) ? parsed : AlertStatus.Open;
        var severity = Severity.ParseOrDefault(alert.Severity).Name;
        var changedAt = Time.ToUtc(message.At);
        var body = message.Alert.GetRawText();

        if (!copies.TryGetValue(alert.Id, out var copy))
        {
            copy = Alert.Create(alert.Id, alert.RuleId, alert.IncidentId, status, severity, body, changedAt);
            copies[alert.Id] = copy;
            await _alerts.AddAsync(copy, cancellationToken);

            return true;
        }

        return copy.Apply(status, severity, alert.IncidentId, body, changedAt);
    }

    // Who gets what for one change, from the configuration alone; null (with the reason logged) when nobody is notified.
    private PlanDraft? Draft(AlertDto alert, string changeId, ConfigSnapshot config, DateTime now)
    {
        if (alert.RuleId is null || !config.Rules.TryGetValue(alert.RuleId, out var rule) || !rule.Rule.Enabled)
        {
            _logger.LogWarning("Alert {AlertId} has no enabled rule copy ({RuleId}); nobody is notified", alert.Id, alert.RuleId);

            return null;
        }

        var route = ToRoute(rule.Body?.Route);
        var severity = Severity.ParseOrDefault(alert.Severity);
        var recipients = _recipients.Resolve(rule.Rule.Id, route, severity, config.Memberships, config.Users, config.Preferences);
        if (recipients.Count == 0)
        {
            _logger.LogWarning("Rule {RuleId} routes alert {AlertId} to nobody known yet; nobody is notified", rule.Rule.Id, alert.Id);

            return null;
        }

        var runbook = config.FindActiveRunbook(alert.RunbookId ?? rule.Body?.RunbookId);
        var policy = new EscalationPolicy(
            TimeSpan.FromMinutes(_escalation.PushDelayMinutes),
            TimeSpan.FromMinutes(_escalation.OnCallCallDelayMinutes));
        var entries = recipients
            .SelectMany(recipient => _ladder.Plan(recipient, severity, policy, now).Select(planned => new PlanEntry(
                recipient,
                planned,
                NotificationId.For(alert.Id, recipient.UserId, planned.Channel.Name, changeId))))
            .ToList();

        return new PlanDraft(
            changeId,
            alert.IncidentId ?? ReadEventId(alert) ?? alert.Id,
            rule.Body?.WindowS is > 0 ? rule.Body.WindowS.Value : DefaultWindowSeconds,
            BuildContent(alert, severity, runbook),
            entries);
    }

    // Returns true when at least one planned row is due at once.
    private async Task<bool> PlanAsync(AlertDto alert, PlanDraft? draft, BatchState batch, DateTime now, CancellationToken cancellationToken)
    {
        if (draft is null)
        {
            return false;
        }

        // Rows committed before this batch and rows planned earlier in it both count.
        var since = now.AddSeconds(-draft.WindowSeconds);
        var alreadyNotified = batch.Notified
            .Where(entry => entry.GroupKey == draft.GroupKey && entry.AlertId != alert.Id && entry.CreatedAt >= since)
            .Select(entry => entry.UserId)
            .ToHashSet();

        var dueNow = false;
        foreach (var (recipient, planned, id) in draft.Entries)
        {
            if (batch.Existing.Contains(id.Value))
            {
                continue;
            }

            var notification = Notification.Create(
                id,
                alert.Id,
                alert.IncidentId,
                draft.GroupKey,
                recipient,
                planned.Channel,
                draft.ChangeId,
                planned.Step,
                $"{recipient.Why}; {planned.Reason}",
                draft.Content,
                planned.SendAt,
                now);

            // One message per person per incident (AC-5.3); the UI card is never noise.
            if (alreadyNotified.Contains(recipient.UserId) && planned.Channel != Channel.Ui)
            {
                notification.Suppress("already notified about this incident", now);
                await _timeline.AppendAsync(notification, AlertEventKinds.NotificationSuppressed, now, cancellationToken);
            }
            else
            {
                dueNow |= planned.SendAt <= now;
                if (planned.Channel != Channel.Ui)
                {
                    batch.Notified.Add(new NotifiedEntry(draft.GroupKey, recipient.UserId, alert.Id, now));
                }
            }

            batch.Open.Add(notification);
            await _notifications.AddAsync(notification, cancellationToken);
        }

        return dueNow;
    }

    // Covers rows committed before this batch and rows planned earlier in it.
    private async Task CancelOpenAsync(string alertId, string reason, BatchState batch, DateTime now, CancellationToken cancellationToken)
    {
        foreach (var notification in batch.Open.Where(notification => notification.AlertId == alertId && notification.IsOpen))
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

    private sealed record PlanEntry(Recipient Recipient, PlannedDelivery Planned, NotificationId Id);

    private sealed record PlanDraft(
        string ChangeId,
        string GroupKey,
        int WindowSeconds,
        NotificationContent Content,
        IReadOnlyList<PlanEntry> Entries);

    // What one batch knows beyond its own records: read once up front, then extended as records are applied.
    private sealed class BatchState
    {
        public IReadOnlySet<string> Existing { get; set; } = new HashSet<string>();

        public List<NotifiedEntry> Notified { get; } = [];

        // Candidates for cancellation; Cancel only touches the ones still open.
        public List<Notification> Open { get; } = [];
    }
}
