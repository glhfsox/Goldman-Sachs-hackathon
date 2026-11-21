using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AlertApi.Application.DTOs.Messages;
using AlertApi.Application.DTOs.Requests;
using AlertApi.Application.DTOs.Responses;
using AlertApi.Application.Exceptions;
using AlertApi.Application.Interfaces;
using AlertApi.Application.Serialization;
using AlertApi.Application.Validation;
using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;

namespace AlertApi.Application.Services;

public sealed partial class RunbookService : IRunbookService
{
    private const string ConfigKind = "runbook";
    private const int MaxIdLength = 60;

    private readonly IRunbookRepository _repository;
    private readonly IReferenceDataQueries _queries;
    private readonly IConfigChangeOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public RunbookService(
        IRunbookRepository repository,
        IReferenceDataQueries queries,
        IConfigChangeOutbox outbox,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _queries = queries;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<RunbookResponse>> GetAllAsync(string? teamId, CancellationToken cancellationToken = default)
    {
        var runbooks = await _repository.GetAllAsync(teamId, cancellationToken);

        return runbooks.Select(RunbookResponse.From).ToList();
    }

    public async Task<RunbookResponse> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return RunbookResponse.From(await GetRequiredAsync(id, cancellationToken));
    }

    // The runbook starts as a draft; the ai-worker converts it and saves the canonical form with PUT.
    public async Task<RunbookResponse> CreateAsync(CreateRunbookRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var problems = RunbookValidator.ValidateCreate(request.TeamId, request.Title, request.Format, request.Source);

        if (problems.Count > 0)
        {
            throw new InvalidRunbookException(problems);
        }

        var id = await FreeIdAsync(Slug(request.Title!), cancellationToken);
        var runbook = Runbook.Create(id, request.TeamId!, request.Title!, request.Format!, request.Source!, _timeProvider.GetUtcNow());

        await _repository.AddAsync(runbook, cancellationToken);

        return await PublishAsync(runbook, cancellationToken);
    }

    public async Task<RunbookResponse> CreateAcceptedAsync(
        string teamId,
        string title,
        JsonElement canonical,
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (canonical.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidRunbookException([new ProblemItem("canonical", "must be an object")]);
        }

        var id = await FreeIdAsync(Slug(title), cancellationToken);

        // The id comes from the title like any new runbook, so the id the proposal's canonical carries is replaced.
        var node = JsonNode.Parse(canonical.GetRawText())!.AsObject();
        node["id"] = id;
        var text = node.ToJsonString();
        var problems = RunbookValidator.ValidateCanonical(RecordJson.ToElement(text), id);

        if (problems.Count > 0)
        {
            throw new InvalidRunbookException(problems);
        }

        var now = _timeProvider.GetUtcNow();
        var runbook = Runbook.Create(id, teamId, title, "canonical", text, now);
        runbook.SetCanonical(text, now);
        runbook.SetStatus(Runbook.Active, now);
        runbook.Accept(userId, now);

        await _repository.AddAsync(runbook, cancellationToken);

        return await PublishAsync(runbook, cancellationToken);
    }

    public async Task<RunbookResponse> UpdateAsync(string id, UpdateRunbookRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var runbook = await GetRequiredAsync(id, cancellationToken);
        var canonical = request.Canonical is { ValueKind: not JsonValueKind.Null } element ? element : (JsonElement?)null;
        var problems = canonical is { } value ? RunbookValidator.ValidateCanonical(value, id) : [];

        if (request.Status is not null)
        {
            problems.AddRange(RunbookValidator.ValidateStatus(request.Status));
        }

        if (problems.Count == 0 && request.Status == Runbook.Active && canonical is null && runbook.Canonical is null)
        {
            problems.Add(new ProblemItem("status", "a runbook needs a canonical form before it is active"));
        }

        if (problems.Count > 0)
        {
            throw new InvalidRunbookException(problems);
        }

        var now = _timeProvider.GetUtcNow();

        if (canonical is { } accepted)
        {
            runbook.SetCanonical(accepted.GetRawText(), now);
        }

        if (request.Status is not null)
        {
            runbook.SetStatus(request.Status, now);
        }

        return await PublishAsync(runbook, cancellationToken);
    }

    public async Task<RunbookResponse> AcceptAsync(string id, string? userId, int? version, CancellationToken cancellationToken = default)
    {
        if (version is null)
        {
            throw new InvalidRunbookException([new ProblemItem("version", "must be the version the person reviewed")]);
        }

        if (string.IsNullOrWhiteSpace(userId) || !await _queries.UserExistsAsync(userId, cancellationToken))
        {
            throw new PersonRequiredException(userId, "accept a runbook");
        }

        var runbook = await GetRequiredAsync(id, cancellationToken);

        if (runbook.Status != Runbook.Active || runbook.Canonical is null)
        {
            throw new RunbookStateConflictException(id, "only an active runbook with a canonical form can be accepted");
        }

        // The person accepts the graph they saw, never a newer one saved meanwhile.
        // shortcut: no concurrency token, so a PUT racing this request by milliseconds can still lose; add one before auto-start (#82 step 4).
        if (runbook.Version != version)
        {
            throw new RunbookStateConflictException(id, $"version {version} is not the current version {runbook.Version}");
        }

        runbook.Accept(userId, _timeProvider.GetUtcNow());
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return RunbookResponse.From(runbook);
    }

    private async Task<RunbookResponse> PublishAsync(Runbook runbook, CancellationToken cancellationToken)
    {
        var response = RunbookResponse.From(runbook);

        _outbox.Add(
            ConfigKind,
            runbook.Id,
            runbook.UpdatedAt,
            new RunbookConfigBody(
                response.Id,
                response.TeamId,
                response.Title,
                response.Format,
                response.Source,
                response.Canonical,
                response.Status,
                response.Version));
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return response;
    }

    private async Task<Runbook> GetRequiredAsync(string id, CancellationToken cancellationToken)
    {
        return await _repository.GetByIdAsync(id, cancellationToken)
            ?? throw new RunbookNotFoundException(id);
    }

    private async Task<string> FreeIdAsync(string slug, CancellationToken cancellationToken)
    {
        var id = slug;

        for (var suffix = 2; await _repository.ExistsAsync(id, cancellationToken); suffix++)
        {
            id = $"{slug}-{suffix}";
        }

        return id;
    }

    private static string Slug(string title)
    {
        var slug = NonSlugPattern().Replace(title.ToLowerInvariant(), "-").Trim('-');

        if (slug.Length > MaxIdLength)
        {
            slug = slug[..MaxIdLength].TrimEnd('-');
        }

        return slug.Length == 0 ? "runbook" : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugPattern();
}
