using AlertApi.Application.DTOs.Requests;
using AlertApi.Application.DTOs.Responses;
using AlertApi.Application.Exceptions;
using AlertApi.Application.Interfaces;
using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;

namespace AlertApi.Application.Services;

public sealed class RunbookStartService : IRunbookStartService
{
    private readonly IRunbookStartRepository _starts;
    private readonly IRunbookRepository _runbooks;
    private readonly IRuleService _rules;
    private readonly IReferenceDataQueries _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public RunbookStartService(
        IRunbookStartRepository starts,
        IRunbookRepository runbooks,
        IRuleService rules,
        IReferenceDataQueries queries,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _starts = starts;
        _runbooks = runbooks;
        _rules = rules;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<RunbookStartResponse>> GetAllAsync(string? ruleId, string? runbookId, CancellationToken cancellationToken = default)
    {
        var starts = await _starts.GetAllAsync(ruleId, runbookId, cancellationToken);

        return starts.Select(RunbookStartResponse.From).ToList();
    }

    public async Task<RunbookStartResponse> PutAsync(
        string ruleId,
        string runbookId,
        PutRunbookStartRequest request,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Only a person may turn automation on; the ai-worker's judge can propose it, never set it.
        if (string.IsNullOrWhiteSpace(userId) || !await _queries.UserExistsAsync(userId, cancellationToken))
        {
            throw new PersonRequiredException(userId, "set how runs start");
        }

        if (request.Start is not (RunbookStart.AutoMode or RunbookStart.ApprovalMode))
        {
            throw new InvalidRunbookStartException([new ProblemItem("start", "must be 'auto' or 'approval'")]);
        }

        var row = await StageAsync(ruleId, runbookId, request.Start, userId, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return RunbookStartResponse.From(row);
    }

    public async Task<RunbookStart> StageAsync(string ruleId, string runbookId, string start, string userId, CancellationToken cancellationToken = default)
    {
        // Throws RuleNotFoundException for an unknown rule.
        await _rules.GetByIdAsync(ruleId, cancellationToken);

        if (!await _runbooks.ExistsAsync(runbookId, cancellationToken))
        {
            throw new RunbookNotFoundException(runbookId);
        }

        var now = _timeProvider.GetUtcNow();
        var row = await _starts.GetTrackedAsync(ruleId, runbookId, cancellationToken);

        if (row is null)
        {
            row = RunbookStart.Create(ruleId, runbookId, start, userId, now);
            await _starts.AddAsync(row, cancellationToken);
        }
        else
        {
            row.Set(start, userId, now);
        }

        return row;
    }
}
