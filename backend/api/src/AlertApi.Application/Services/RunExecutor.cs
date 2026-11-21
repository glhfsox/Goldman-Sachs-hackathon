using System.Text.Json;

using Microsoft.Extensions.Logging;

using AlertApi.Application.Actions;
using AlertApi.Application.Exceptions;
using AlertApi.Application.Interfaces;
using AlertApi.Application.Runs;
using AlertApi.Application.Serialization;
using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;

namespace AlertApi.Application.Services;

public sealed class RunExecutor : IRunExecutor
{
    public const string Actor = "runbook-executor";

    // What is left of a lease after the step's own timeout, to save the result.
    private static readonly TimeSpan LeaseMargin = TimeSpan.FromSeconds(15);

    private readonly IRunRepository _runs;
    private readonly IAlertEventRepository _events;
    private readonly IActionRegistry _registry;
    private readonly IUiPublisher _ui;
    private readonly IUnitOfWork _unitOfWork;
    private readonly RunExecutorOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RunExecutor> _logger;

    public RunExecutor(
        IRunRepository runs,
        IAlertEventRepository events,
        IActionRegistry registry,
        IUiPublisher ui,
        IUnitOfWork unitOfWork,
        RunExecutorOptions options,
        TimeProvider timeProvider,
        ILogger<RunExecutor> logger)
    {
        _runs = runs;
        _events = events;
        _registry = registry;
        _ui = ui;
        _unitOfWork = unitOfWork;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<bool> ExecuteNextAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var node = await _runs.ClaimReadyAsync(now, _options.Lease, cancellationToken);

        if (node is not null)
        {
            var approver = await _runs.GetApproverAsync(node.RunId, cancellationToken);
            var result = await PerformAsync(node, approver ?? Actor, cancellationToken);

            await CompleteAsync(node, result, cancellationToken);

            return true;
        }

        // The worker that held this node died after the call may have gone out; the idempotency key makes a retry safe.
        var abandoned = await _runs.ClaimExpiredAsync(now, _options.Lease, cancellationToken);

        if (abandoned is null)
        {
            return false;
        }

        _logger.LogWarning("Node {NodeId} lost its worker; marking it failed.", abandoned.Id);
        await CompleteAsync(
            abandoned,
            ActionResult.Failure("the worker running this step stopped before it reported a result"),
            cancellationToken);

        return true;
    }

    // A plugin must never take the executor down: whatever it throws becomes a failed step.
    private async Task<ActionResult> PerformAsync(ActionRun node, string actor, CancellationToken cancellationToken)
    {
        try
        {
            if (node.Action is null || node.Request is null)
            {
                return ActionResult.Failure("the step has no request to send");
            }

            if (!_registry.TryGetPlugin(node.Action, out var plugin))
            {
                return ActionResult.Failure($"action '{node.Action}' is not registered");
            }

            var request = JsonSerializer.Deserialize<ActionRequest>(node.Request, RecordJson.Options)
                ?? throw new InvalidOperationException("the frozen request is empty");
            var timeout = TimeSpan.FromSeconds(node.TimeoutSeconds ?? plugin.DefaultTimeout.TotalSeconds);
            var context = new ActionContext(
                node.AlertId,
                node.RunId,
                node.NodeId,
                node.IdempotencyKey,
                actor,
                Min(timeout, _options.Lease - LeaseMargin));

            return await plugin.RunAsync(request, context, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Action {Action} of node {NodeId} threw.", node.Action, node.Id);

            return ActionResult.Failure($"{node.Action} failed: {exception.Message}");
        }
    }

    // The run is locked first, so two steps that finish together plan one after the other.
    private async Task CompleteAsync(ActionRun node, ActionResult result, CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);
            await _runs.LockAsync(node.RunId, cancellationToken);

            var run = await _runs.GetByIdAsync(node.RunId, cancellationToken)
                ?? throw new RunNotFoundException(node.RunId);
            var nodes = await _runs.GetNodesAsync(node.RunId, cancellationToken);
            var now = _timeProvider.GetUtcNow();

            node.Finish(result.Ok, JsonSerializer.Serialize(result, RecordJson.Options), now);
            await AddEventAsync($"node-finished:{node.Id}:{node.Attempt}", node, "action_finished", now, cancellationToken);

            // A cancelled run keeps the result of a step that was already running; nothing moves on.
            var ended = run.IsLive ? RunProgress.Apply(run, nodes, now) : null;

            if (ended is not null)
            {
                await _events.AddAsync(
                    AlertEvent.Create($"run-finished:{run.Id}", run.AlertId, now, Actor, $"run_{ended}", RunData(run)),
                    cancellationToken);
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await _ui.PublishTimelineAsync(run.AlertId, cancellationToken);
        }
        catch (ConcurrentUpdateException)
        {
            // Another worker took the node after the lease ran out and reports it instead.
            _logger.LogWarning("Node {NodeId} was taken over before its result was saved; dropping this one.", node.Id);
        }
    }

    private async Task AddEventAsync(string id, ActionRun node, string kind, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var data = JsonSerializer.Serialize(
            new
            {
                ActionRun = new
                {
                    node.Id,
                    node.RunbookId,
                    node.StepIndex,
                    node.Action,
                    Params = node.Params is null ? (JsonElement?)null : RecordJson.ToElement(node.Params),
                    node.Status,
                    Result = node.Result is null ? (JsonElement?)null : RecordJson.ToElement(node.Result)
                },
                node.RunId,
                node.NodeId,
                node.Attempt
            },
            RecordJson.Options);

        await _events.AddAsync(AlertEvent.Create(id, node.AlertId, at, Actor, kind, data), cancellationToken);
    }

    private static string RunData(RunbookRun run)
    {
        return JsonSerializer.Serialize(
            new { Run = new { run.Id, run.RunbookId, run.Risk, run.Trigger, run.Status, Node = (string?)null } },
            RecordJson.Options);
    }

    private static TimeSpan Min(TimeSpan first, TimeSpan second)
    {
        return first < second ? first : second;
    }
}
