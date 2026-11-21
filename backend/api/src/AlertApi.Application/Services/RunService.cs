using System.Text.Json;

using AlertApi.Application.DTOs.Requests;
using AlertApi.Application.DTOs.Responses;
using AlertApi.Application.Exceptions;
using AlertApi.Application.Interfaces;
using AlertApi.Application.Runs;
using AlertApi.Application.Serialization;
using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;

namespace AlertApi.Application.Services;

public sealed class RunService : IRunService
{
    private const string AiWorker = "ai-worker";
    private const int MaxReasons = 6;
    private const int DefaultListLimit = 100;
    private const int MaxListLimit = 500;

    private readonly IRunRepository _runs;
    private readonly IAlertRepository _alerts;
    private readonly IAlertEventRepository _events;
    private readonly IRunbookRepository _runbooks;
    private readonly IReferenceDataQueries _queries;
    private readonly IActionCatalogProvider _catalog;
    private readonly IUiPublisher _ui;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public RunService(
        IRunRepository runs,
        IAlertRepository alerts,
        IAlertEventRepository events,
        IRunbookRepository runbooks,
        IReferenceDataQueries queries,
        IActionCatalogProvider catalog,
        IUiPublisher ui,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _runs = runs;
        _alerts = alerts;
        _events = events;
        _runbooks = runbooks;
        _queries = queries;
        _catalog = catalog;
        _ui = ui;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<RunPreviewResponse> PreviewAsync(string alertId, string? runbookId, CancellationToken cancellationToken = default)
    {
        var alert = await GetAlertAsync(alertId, cancellationToken);
        var runbook = await GetRunnableRunbookAsync(alert, runbookId, cancellationToken);
        var draft = RunDrafter.Draft(alert, RecordJson.ToElement(runbook.Canonical!), await _catalog.GetAsync(cancellationToken));

        return RunPreviewResponse.From(runbook.Id, runbook.Version, draft);
    }

    public async Task<RunResponse> StartAsync(
        string alertId,
        StartRunRequest request,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await RequirePersonAsync(userId, "start a run", cancellationToken);
        var alert = await GetAlertAsync(alertId, cancellationToken);
        var runbook = await GetRunnableRunbookAsync(alert, request.RunbookId, cancellationToken);

        if (await _runs.HasLiveRunAsync(alertId, cancellationToken))
        {
            throw new LiveRunExistsException(alertId);
        }

        var draft = RunDrafter.Draft(alert, RecordJson.ToElement(runbook.Canonical!), await _catalog.GetAsync(cancellationToken));
        var now = _timeProvider.GetUtcNow();
        var run = RunbookRun.StartByHand(
            $"run-{Guid.NewGuid():N}"[..16],
            alert.Id,
            alert.RuleId,
            alert.TeamId,
            runbook.Id,
            runbook.Version,
            draft.Risk,
            user,
            now);
        var nodes = RunNodes.Create(run, draft, now);
        var ended = RunProgress.Apply(run, nodes, now);

        await _runs.AddAsync(run, nodes, cancellationToken);
        await AddEventAsync($"run-started:{run.Id}", run, user, "run_started", now, cancellationToken);

        if (ended is not null)
        {
            await AddEventAsync($"run-finished:{run.Id}", run, user, $"run_{ended}", now, cancellationToken);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await _ui.PublishTimelineAsync(alert.Id, cancellationToken);

        return RunResponse.From(run, nodes);
    }

    public async Task<RunResponse> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var run = await GetRunAsync(id, cancellationToken);

        return RunResponse.From(run, await _runs.GetNodesAsync(id, cancellationToken));
    }

    public async Task<RunResponse> CancelAsync(string id, string? userId, CancellationToken cancellationToken = default)
    {
        var user = await RequirePersonAsync(userId, "cancel a run", cancellationToken);
        var run = await GetRunAsync(id, cancellationToken);

        if (!run.IsLive)
        {
            throw new RunStateConflictException(id, $"it is {run.Status}, so it cannot be cancelled");
        }

        var nodes = await _runs.GetNodesAsync(id, cancellationToken);
        var now = _timeProvider.GetUtcNow();

        RunProgress.Cancel(run, nodes, user, now);
        await AddEventAsync($"run-finished:{run.Id}", run, user, "run_cancelled", now, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await _ui.PublishTimelineAsync(run.AlertId, cancellationToken);

        return RunResponse.From(run, nodes);
    }

    public async Task<RunNodeResponse> TickAsync(string id, string node, string? userId, CancellationToken cancellationToken = default)
    {
        var user = await RequirePersonAsync(userId, "tick a step", cancellationToken);
        var run = await GetRunAsync(id, cancellationToken);
        var nodes = await _runs.GetNodesAsync(id, cancellationToken);
        var target = nodes.FirstOrDefault(candidate => candidate.NodeId == node)
            ?? throw new RunNodeNotFoundException(id, node);

        if (!run.IsLive)
        {
            throw new RunStateConflictException(id, $"it is {run.Status}, so no step can be ticked");
        }

        if (!target.IsManual || target.Status != ActionRun.Pending)
        {
            throw new RunStateConflictException(id, $"step '{node}' is not a text step waiting for a person");
        }

        var now = _timeProvider.GetUtcNow();

        target.Tick(now);
        await AddEventAsync($"node-done:{id}:{node}", run, user, "node_succeeded", now, cancellationToken, node);

        var ended = RunProgress.Apply(run, nodes, now);

        if (ended is not null)
        {
            await AddEventAsync($"run-finished:{run.Id}", run, user, $"run_{ended}", now, cancellationToken);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await _ui.PublishTimelineAsync(run.AlertId, cancellationToken);

        return RunNodeResponse.From(target);
    }

    public async Task<RunResponse> RiskCheckAsync(
        string id,
        RiskCheckRequest request,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (userId != AiWorker)
        {
            throw new RiskCheckerRequiredException(userId);
        }

        var problems = new List<ProblemItem>();

        if (request.Verdict is not (RunbookRun.Clear or RunbookRun.Hold))
        {
            problems.Add(new ProblemItem("verdict", "must be 'clear' or 'hold'"));
        }

        if (request.Confidence is null or < 0 or > 1)
        {
            problems.Add(new ProblemItem("confidence", "must be a number from 0 to 1"));
        }

        var reasons = (request.Reasons ?? []).Select(reason => reason?.Trim() ?? string.Empty).ToList();

        if (reasons.Count > MaxReasons || reasons.Any(reason => reason.Length == 0))
        {
            problems.Add(new ProblemItem("reasons", $"must be at most {MaxReasons} non-empty sentences"));
        }

        if (problems.Count > 0)
        {
            throw new InvalidRunRequestException(problems);
        }

        // The run row is locked until the save, so a verdict and the scheduler's start cannot both win.
        await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        await _runs.LockAsync(id, cancellationToken);

        var run = await GetRunAsync(id, cancellationToken);

        if (run.RiskCheckVerdict == request.Verdict)
        {
            return RunResponse.From(run, await _runs.GetNodesAsync(id, cancellationToken));
        }

        if (run.Status != RunbookRun.Scheduled)
        {
            throw new RunStateConflictException(id, $"it is {run.Status}, so it takes no risk check now");
        }

        var now = _timeProvider.GetUtcNow();

        run.RecordRiskCheck(request.Verdict!, request.Confidence, JsonSerializer.Serialize(reasons, RecordJson.Options), userId, now);

        if (request.Verdict == RunbookRun.Hold)
        {
            await _events.AddAsync(RunTimeline.Create($"run-held:{run.Id}", run, userId, "run_held", now, reasons: reasons), cancellationToken);
        }
        else
        {
            await _events.AddAsync(RunTimeline.Create($"run-cleared:{run.Id}", run, userId, "run_cleared", now, reasons: reasons), cancellationToken);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await _ui.PublishTimelineAsync(run.AlertId, cancellationToken);

        return RunResponse.From(run, await _runs.GetNodesAsync(id, cancellationToken));
    }

    public async Task<RunResponse> ApproveAsync(string id, string? userId, CancellationToken cancellationToken = default)
    {
        var user = await RequirePersonAsync(userId, "approve a run", cancellationToken);

        await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        await _runs.LockAsync(id, cancellationToken);

        var run = await GetRunAsync(id, cancellationToken);

        if (run.Status != RunbookRun.WaitingApproval)
        {
            throw new RunStateConflictException(id, $"it is {run.Status}, so there is nothing to approve");
        }

        var now = _timeProvider.GetUtcNow();

        run.Approve(user, now);
        await AddEventAsync($"run-started:{run.Id}", run, user, "run_started", now, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await _ui.PublishTimelineAsync(run.AlertId, cancellationToken);

        return RunResponse.From(run, await _runs.GetNodesAsync(id, cancellationToken));
    }

    public async Task<IReadOnlyList<RunResponse>> ListAsync(
        string? riskCheck,
        DateTimeOffset? finishedSince,
        int? limit,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit ?? DefaultListLimit, 1, MaxListLimit);
        IReadOnlyList<RunbookRun> runs;

        if (riskCheck == "pending")
        {
            runs = await _runs.ListPendingRiskCheckAsync(take, cancellationToken);
        }
        else if (finishedSince is not null)
        {
            runs = await _runs.ListFinishedSinceAsync(finishedSince.Value, take, cancellationToken);
        }
        else
        {
            throw new InvalidRunRequestException([new ProblemItem("query", "needs risk_check=pending or finished_since=<time>")]);
        }

        var nodes = (await _runs.GetNodesForRunsAsync(runs.Select(run => run.Id).ToList(), cancellationToken))
            .ToLookup(node => node.RunId);

        return runs.Select(run => RunResponse.From(run, nodes[run.Id].ToList())).ToList();
    }

    private async Task AddEventAsync(
        string id,
        RunbookRun run,
        string actor,
        string kind,
        DateTimeOffset at,
        CancellationToken cancellationToken,
        string? node = null)
    {
        await _events.AddAsync(RunTimeline.Create(id, run, actor, kind, at, node), cancellationToken);
    }

    private async Task<Alert> GetAlertAsync(string id, CancellationToken cancellationToken)
    {
        return await _alerts.GetByIdAsync(id, cancellationToken)
            ?? throw new AlertNotFoundException(id);
    }

    private async Task<RunbookRun> GetRunAsync(string id, CancellationToken cancellationToken)
    {
        return await _runs.GetByIdAsync(id, cancellationToken)
            ?? throw new RunNotFoundException(id);
    }

    // Only an active runbook version that a person accepted runs, and only on an open alert of its own team.
    private async Task<Runbook> GetRunnableRunbookAsync(Alert alert, string? runbookId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runbookId))
        {
            throw new InvalidRunRequestException([new ProblemItem("runbook_id", "is required")]);
        }

        var runbook = await _runbooks.GetByIdAsync(runbookId, cancellationToken)
            ?? throw new RunbookNotFoundException(runbookId);

        if (alert.Status == Alert.Resolved)
        {
            throw new AlertStateConflictException(alert.Id, alert.Status, "given a new run");
        }

        if (runbook.Status != Runbook.Active || runbook.Canonical is null)
        {
            throw new RunbookStateConflictException(runbook.Id, "only an active runbook can run");
        }

        if (runbook.AcceptedBy is null)
        {
            throw new RunbookStateConflictException(runbook.Id, $"version {runbook.Version} has not been accepted by a person yet");
        }

        if (runbook.TeamId != alert.TeamId)
        {
            throw new RunbookStateConflictException(runbook.Id, $"it belongs to team '{runbook.TeamId}', not '{alert.TeamId}'");
        }

        return runbook;
    }

    private async Task<string> RequirePersonAsync(string? userId, string what, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId) || !await _queries.UserExistsAsync(userId, cancellationToken))
        {
            throw new PersonRequiredException(userId, what);
        }

        return userId;
    }
}
