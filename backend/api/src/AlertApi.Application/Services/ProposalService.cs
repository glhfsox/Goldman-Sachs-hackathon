using System.Text.Json;
using System.Text.Json.Nodes;

using AlertApi.Application.DTOs.Requests;
using AlertApi.Application.DTOs.Responses;
using AlertApi.Application.Exceptions;
using AlertApi.Application.Interfaces;
using AlertApi.Application.Serialization;
using AlertApi.Application.Validation;
using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;

namespace AlertApi.Application.Services;

public sealed class ProposalService : IProposalService
{
    private const int MaxLimit = 200;
    private const string AiWorker = "ai-worker";

    private readonly IProposalRepository _proposals;
    private readonly IAlertRepository _alerts;
    private readonly IAlertEventRepository _events;
    private readonly IReferenceDataQueries _queries;
    private readonly IRuleService _rules;
    private readonly IRunbookService _runbooks;
    private readonly IActionCatalogProvider _catalog;
    private readonly IUiPublisher _ui;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public ProposalService(
        IProposalRepository proposals,
        IAlertRepository alerts,
        IAlertEventRepository events,
        IReferenceDataQueries queries,
        IRuleService rules,
        IRunbookService runbooks,
        IActionCatalogProvider catalog,
        IUiPublisher ui,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _proposals = proposals;
        _alerts = alerts;
        _events = events;
        _queries = queries;
        _rules = rules;
        _runbooks = runbooks;
        _catalog = catalog;
        _ui = ui;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<CreatedProposal> CreateAsync(
        string alertId,
        CreateProposalRequest request,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var author = await RequireAuthorAsync(userId, cancellationToken);

        if (await _alerts.GetByIdAsync(alertId, cancellationToken) is null)
        {
            throw new AlertNotFoundException(alertId);
        }

        var problems = ProposalValidator.Validate(request.Kind, request.Body, await _catalog.GetAsync(cancellationToken));

        if (problems.Count > 0)
        {
            throw new InvalidProposalException(problems);
        }

        var body = request.Body!.Value.GetRawText();

        // The ai-worker retries a request it did not see answered; the same open proposal is returned, not filed twice.
        var open = await _proposals.GetOpenAsync(alertId, request.Kind!, cancellationToken);
        var existing = open.FirstOrDefault(proposal => JsonNode.DeepEquals(JsonNode.Parse(proposal.Body), JsonNode.Parse(body)));

        if (existing is not null)
        {
            return new CreatedProposal(ProposalResponse.From(existing), Created: false);
        }

        var now = _timeProvider.GetUtcNow();
        var proposal = Proposal.Create(Guid.NewGuid().ToString(), alertId, request.Kind!, body, author, now);

        await _proposals.AddAsync(proposal, cancellationToken);
        await AddTimelineRowAsync("proposal-created", proposal, "proposal_created", author, now, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await _ui.PublishTimelineAsync(alertId, cancellationToken);

        return new CreatedProposal(ProposalResponse.From(proposal), Created: true);
    }

    public async Task<IReadOnlyList<ProposalResponse>> GetAllAsync(string? status, int limit, CancellationToken cancellationToken = default)
    {
        var proposals = await _proposals.GetAllAsync(status, Math.Clamp(limit, 1, MaxLimit), cancellationToken);

        return proposals.Select(ProposalResponse.From).ToList();
    }

    public async Task<ProposalResponse> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return ProposalResponse.From(await GetRequiredAsync(id, cancellationToken));
    }

    public async Task<ProposalResponse> AcceptAsync(string id, string? userId, CancellationToken cancellationToken = default)
    {
        var user = await RequirePersonAsync(userId, cancellationToken);
        var proposal = await GetRequiredOpenAsync(id, cancellationToken);

        // shortcut: the effect and the decision are two saves; a crash between them leaves the proposal open, and
        // accepting it again repeats the effect (a rule_severity is the same value; a runbook would be saved twice).
        await ApplyAsync(proposal, user, cancellationToken);

        return await DecideAsync(proposal, accepted: true, user, cancellationToken);
    }

    public async Task<ProposalResponse> RejectAsync(string id, string? userId, CancellationToken cancellationToken = default)
    {
        var user = await RequirePersonAsync(userId, cancellationToken);
        var proposal = await GetRequiredOpenAsync(id, cancellationToken);

        return await DecideAsync(proposal, accepted: false, user, cancellationToken);
    }

    private async Task ApplyAsync(Proposal proposal, string userId, CancellationToken cancellationToken)
    {
        var body = RecordJson.ToElement(proposal.Body);

        switch (proposal.Kind)
        {
            case ProposalValidator.RuleSeverity:
                await ApplyRuleSeverityAsync(body, cancellationToken);
                break;
            case ProposalValidator.Runbook:
                await _runbooks.CreateAcceptedAsync(
                    body.GetProperty("team_id").GetString()!,
                    body.GetProperty("title").GetString()!,
                    body.GetProperty("canonical"),
                    userId,
                    cancellationToken);
                break;
            case ProposalValidator.RunbookStart:
                // Needs the run model (#82 step 3) and a decision on how a rule stores the start mode (#75 question 3).
                throw new ProposalStateConflictException("Accepting a runbook_start proposal is not available yet; reject it or change the rule by hand.");
        }

        // threshold and template have no automatic effect: a person applies them, then accepts.
    }

    private async Task ApplyRuleSeverityAsync(JsonElement body, CancellationToken cancellationToken)
    {
        var ruleId = body.GetProperty("rule_id").GetString()!;
        JsonElement rule;

        try
        {
            rule = await _rules.GetByIdAsync(ruleId, cancellationToken);
        }
        catch (RuleNotFoundException)
        {
            throw new ProposalStateConflictException($"Rule '{ruleId}' no longer exists, so its severity cannot be changed.");
        }

        var node = JsonNode.Parse(rule.GetRawText())!.AsObject();
        node["severity"] = body.GetProperty("severity").GetString();

        await _rules.UpdateAsync(ruleId, RecordJson.ToElement(node.ToJsonString()), cancellationToken);
    }

    private async Task<ProposalResponse> DecideAsync(Proposal proposal, bool accepted, string userId, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        proposal.Decide(accepted, userId, now);
        await AddTimelineRowAsync("proposal-decided", proposal, "proposal_decided", userId, now, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await _ui.PublishTimelineAsync(proposal.AlertId, cancellationToken);

        return ProposalResponse.From(proposal);
    }

    private async Task AddTimelineRowAsync(
        string idPrefix,
        Proposal proposal,
        string kind,
        string actor,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var data = JsonSerializer.Serialize(
            new { Proposal = new { proposal.Id, proposal.Kind, proposal.Status } },
            RecordJson.Options);

        await _events.AddAsync(AlertEvent.Create($"{idPrefix}:{proposal.Id}", proposal.AlertId, at, actor, kind, data), cancellationToken);
    }

    private async Task<Proposal> GetRequiredAsync(string id, CancellationToken cancellationToken)
    {
        return await _proposals.GetByIdAsync(id, cancellationToken)
            ?? throw new ProposalNotFoundException(id);
    }

    private async Task<Proposal> GetRequiredOpenAsync(string id, CancellationToken cancellationToken)
    {
        var proposal = await GetRequiredAsync(id, cancellationToken);

        if (proposal.Status != Proposal.Open)
        {
            throw new ProposalStateConflictException($"Proposal '{id}' is already {proposal.Status}.");
        }

        return proposal;
    }

    // A proposal comes from a person or from the ai-worker; any other name is not a known author.
    private async Task<string> RequireAuthorAsync(string? userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new InvalidUserException([new ProblemItem("X-User", "header is required")]);
        }

        if (userId != AiWorker && !await _queries.UserExistsAsync(userId, cancellationToken))
        {
            throw new UserNotFoundException(userId);
        }

        return userId;
    }

    private async Task<string> RequirePersonAsync(string? userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId) || !await _queries.UserExistsAsync(userId, cancellationToken))
        {
            throw new PersonRequiredException(userId, "accept or reject a proposal");
        }

        return userId;
    }
}
