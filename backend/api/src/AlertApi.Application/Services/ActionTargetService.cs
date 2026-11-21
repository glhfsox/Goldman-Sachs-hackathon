using System.Text.Json;

using AlertApi.Application.Actions;
using AlertApi.Application.DTOs.Requests;
using AlertApi.Application.DTOs.Responses;
using AlertApi.Application.Exceptions;
using AlertApi.Application.Interfaces;
using AlertApi.Application.Validation;
using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;

namespace AlertApi.Application.Services;

public sealed class ActionTargetService : IActionTargetService
{
    private readonly IActionTargetRepository _targets;
    private readonly IRunbookRepository _runbooks;
    private readonly IActionRegistry _registry;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public ActionTargetService(
        IActionTargetRepository targets,
        IRunbookRepository runbooks,
        IActionRegistry registry,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _targets = targets;
        _runbooks = runbooks;
        _registry = registry;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public IReadOnlyList<ActionResponse> GetActions()
    {
        return _registry.Descriptors.Values.OrderBy(action => action.Name).Select(ActionResponse.From).ToList();
    }

    public async Task<IReadOnlyList<ActionTargetResponse>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var targets = await _targets.GetAllAsync(cancellationToken);

        return targets.Select(ActionTargetResponse.From).ToList();
    }

    public async Task<ActionTargetResponse> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var target = await _targets.GetByIdAsync(id, cancellationToken)
            ?? throw new ActionTargetNotFoundException(id);

        return ActionTargetResponse.From(target);
    }

    // Create or replace. A change that would make an active runbook invalid (a removed operation, a dropped origin)
    // is refused with the runbooks it would break, so a target edit never silently breaks a team's runbook.
    public async Task<ActionTargetResponse> PutAsync(string id, PutActionTargetRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var problems = ActionTargetValidator.Validate(id, request);

        if (problems.Count > 0)
        {
            throw new InvalidActionTargetException(problems);
        }

        var origins = request.Origins is { ValueKind: JsonValueKind.Array } array ? array.GetRawText() : "[]";
        var operations = request.Operations!.Value.GetRawText();

        await EnsureNoRunbookBreaksAsync(id, request.BaseUrl!, origins, operations, cancellationToken);

        var now = _timeProvider.GetUtcNow();
        var target = await _targets.GetByIdAsync(id, cancellationToken);

        if (target is null)
        {
            target = ActionTarget.Create(id, request.BaseUrl!, origins, operations, request.SecretRef, now);
            await _targets.AddAsync(target, cancellationToken);
        }
        else
        {
            target.Update(request.BaseUrl!, origins, operations, request.SecretRef, now);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ActionTargetResponse.From(target);
    }

    private async Task EnsureNoRunbookBreaksAsync(
        string id,
        string baseUrl,
        string origins,
        string operations,
        CancellationToken cancellationToken)
    {
        var current = (await _targets.GetAllAsync(cancellationToken)).Select(TargetDefinition.From).ToDictionary(target => target.Id);
        var changed = new Dictionary<string, TargetDefinition>(current)
        {
            [id] = TargetDefinition.From(ActionTarget.Create(id, baseUrl, origins, operations, secretRef: null, _timeProvider.GetUtcNow()))
        };
        var before = new ActionCatalog(_registry.Descriptors, current);
        var after = new ActionCatalog(_registry.Descriptors, changed);
        var broken = new List<string>();

        foreach (var runbook in await _runbooks.GetAllAsync(teamId: null, cancellationToken))
        {
            if (runbook.Status != Runbook.Active || runbook.Canonical is null)
            {
                continue;
            }

            using var canonical = JsonDocument.Parse(runbook.Canonical);

            if (RunbookValidator.ValidateCanonical(canonical.RootElement, runbook.Id, before).Count == 0
                && RunbookValidator.ValidateCanonical(canonical.RootElement, runbook.Id, after).Count > 0)
            {
                broken.Add(runbook.Id);
            }
        }

        if (broken.Count > 0)
        {
            throw new ActionTargetInUseException(id, broken);
        }
    }
}
