using Microsoft.Extensions.Logging;

using AlertApi.Application.Interfaces;
using AlertApi.Application.Runs;
using AlertApi.Application.Serialization;
using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;

namespace AlertApi.Application.Services;

public sealed class RunAutomationService : IRunAutomation
{
    private const string Actor = "runbook-scheduler";
    private const string UnansweredReasons = "[\"risk check did not answer\"]";

    private readonly IRunRepository _runs;
    private readonly IRunbookRepository _runbooks;
    private readonly IRunbookStartRepository _starts;
    private readonly IAlertEventRepository _events;
    private readonly IActionCatalogProvider _catalog;
    private readonly IUiPublisher _ui;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly RunAutomationOptions _options;
    private readonly ILogger<RunAutomationService> _logger;

    public RunAutomationService(
        IRunRepository runs,
        IRunbookRepository runbooks,
        IRunbookStartRepository starts,
        IAlertEventRepository events,
        IActionCatalogProvider catalog,
        IUiPublisher ui,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        RunAutomationOptions options,
        ILogger<RunAutomationService> logger)
    {
        _runs = runs;
        _runbooks = runbooks;
        _starts = starts;
        _events = events;
        _catalog = catalog;
        _ui = ui;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _options = options;
        _logger = logger;
    }

    public async Task StageStartAsync(Alert alert, string changeId, DateTimeOffset changeAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var now = _timeProvider.GetUtcNow();

        if (alert.Status != Alert.Open || string.IsNullOrWhiteSpace(alert.RunbookId))
        {
            return;
        }

        if (now - changeAt > _options.MaxChangeAge)
        {
            _logger.LogInformation("Alert {AlertId} changed too long ago for an automatic run.", alert.Id);

            return;
        }

        var runbook = await _runbooks.GetByIdAsync(alert.RunbookId, cancellationToken);

        if (runbook is null || runbook.Status != Runbook.Active || runbook.Canonical is null || runbook.TeamId != alert.TeamId)
        {
            _logger.LogInformation("Alert {AlertId} names runbook {RunbookId}, which cannot run.", alert.Id, alert.RunbookId);

            return;
        }

        if (await _runs.HasLiveRunAsync(alert.Id, cancellationToken))
        {
            return;
        }

        RunDraft draft;

        try
        {
            draft = RunDrafter.Draft(alert, RecordJson.ToElement(runbook.Canonical), await _catalog.GetAsync(cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A broken runbook must not stall the alert feed: the consumer would retry this record forever.
            _logger.LogWarning(exception, "Runbook {RunbookId} cannot be drafted for alert {AlertId}; no automatic run.", runbook.Id, alert.Id);

            return;
        }

        var risk = RiskNames.Parse(draft.Risk) ?? Risk.Approval;
        var start = await _starts.GetAsync(alert.RuleId, runbook.Id, cancellationToken);
        var mode = AutoStartPolicy.Decide(risk, runbook.AcceptedBy is not null, start?.Start);

        if (mode is null)
        {
            return;
        }

        var startAt = mode == RunStartMode.Immediately ? now : now + _options.CancelWindow;
        var run = RunbookRun.StartAutomatically(
            $"run-{Guid.NewGuid():N}"[..16],
            alert.Id,
            alert.RuleId,
            alert.TeamId,
            runbook.Id,
            runbook.Version,
            draft.Risk,
            changeId,
            startAt,
            now);
        var nodes = RunNodes.Create(run, draft, now);
        var scheduled = run.Status == RunbookRun.Scheduled;
        var ended = RunProgress.Apply(run, nodes, now);

        await _runs.AddAsync(run, nodes, cancellationToken);
        await _events.AddAsync(
            scheduled
                ? RunTimeline.Create($"run-scheduled:{run.Id}", run, Actor, "run_scheduled", now)
                : RunTimeline.Create($"run-started:{run.Id}", run, Actor, "run_started", now),
            cancellationToken);

        if (ended is not null)
        {
            await _events.AddAsync(RunTimeline.Create($"run-finished:{run.Id}", run, Actor, $"run_{ended}", now), cancellationToken);
        }
    }

    public async Task StageCancelAsync(string alertId, string actor, CancellationToken cancellationToken = default)
    {
        var run = await _runs.GetLiveByAlertIdAsync(alertId, cancellationToken);

        if (run is null)
        {
            return;
        }

        var nodes = await _runs.GetNodesAsync(run.Id, cancellationToken);
        var now = _timeProvider.GetUtcNow();

        RunProgress.Cancel(run, nodes, actor, now);
        await _events.AddAsync(RunTimeline.Create($"run-finished:{run.Id}", run, actor, "run_cancelled", now), cancellationToken);
    }

    public async Task<bool> StartDueAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);

        // A run nobody answered for must not start unchecked: it goes to a person instead.
        var held = _options.RiskCheck
            ? await _runs.HoldUnansweredAsync(now, Actor, UnansweredReasons, cancellationToken)
            : null;

        if (held is not null)
        {
            await _events.AddAsync(
                RunTimeline.Create($"run-held:{held.Id}", held, Actor, "run_held", now, reasons: ["risk check did not answer"]),
                cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await _ui.PublishTimelineAsync(held.AlertId, cancellationToken);

            return true;
        }

        var run = await _runs.ClaimDueScheduledAsync(now, _options.RiskCheck, cancellationToken);

        if (run is null)
        {
            return false;
        }

        await _events.AddAsync(RunTimeline.Create($"run-started:{run.Id}", run, Actor, "run_started", now), cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await _ui.PublishTimelineAsync(run.AlertId, cancellationToken);

        return true;
    }
}
