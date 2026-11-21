using System.Text.Json;

using AlertApi.Application.Exceptions;
using AlertApi.Application.Interfaces;
using AlertApi.Application.Validation;
using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;

namespace AlertApi.Application.Services;

public sealed class RuleService : IRuleService
{
    private readonly IRuleRepository _repository;
    private readonly IConfigChangeOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public RuleService(
        IRuleRepository repository,
        IConfigChangeOutbox outbox,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<JsonElement>> GetAllAsync(string? teamId, CancellationToken cancellationToken = default)
    {
        var rules = await _repository.GetAllAsync(teamId, cancellationToken);

        return rules.Select(rule => ToJson(rule.Body)).ToList();
    }

    public async Task<JsonElement> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return ToJson((await GetRequiredAsync(id, cancellationToken)).Body);
    }

    public async Task<JsonElement> CreateAsync(JsonElement rule, CancellationToken cancellationToken = default)
    {
        var problems = RuleValidator.Validate(rule);

        if (problems.Count > 0)
        {
            throw new InvalidRuleException(problems);
        }

        var id = rule.GetProperty("id").GetString()!;

        await EnsureRuleIsUniqueAsync(id, cancellationToken);

        var created = Rule.Create(id, TeamOf(rule), EnabledOf(rule), rule.GetRawText(), _timeProvider.GetUtcNow());

        await _repository.AddAsync(created, cancellationToken);
        _outbox.Add("rule", id, created.UpdatedAt, rule);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ToJson(created.Body);
    }

    public async Task<JsonElement> UpdateAsync(string id, JsonElement rule, CancellationToken cancellationToken = default)
    {
        var existing = await GetRequiredAsync(id, cancellationToken);
        var problems = RuleValidator.Validate(rule);

        if (problems.Count == 0 && rule.GetProperty("id").GetString() != id)
        {
            problems.Add(new ProblemItem("id", "must match the id in the URL"));
        }

        if (problems.Count > 0)
        {
            throw new InvalidRuleException(problems);
        }

        existing.Update(TeamOf(rule), EnabledOf(rule), rule.GetRawText(), _timeProvider.GetUtcNow());
        _outbox.Add("rule", id, existing.UpdatedAt, rule);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ToJson(existing.Body);
    }

    private async Task<Rule> GetRequiredAsync(string id, CancellationToken cancellationToken)
    {
        return await _repository.GetByIdAsync(id, cancellationToken)
            ?? throw new RuleNotFoundException(id);
    }

    private async Task EnsureRuleIsUniqueAsync(string id, CancellationToken cancellationToken)
    {
        if (await _repository.GetByIdAsync(id, cancellationToken) is not null)
        {
            throw new RuleAlreadyExistsException(id);
        }
    }

    private static string TeamOf(JsonElement rule)
    {
        return rule.GetProperty("team_id").GetString()!;
    }

    // The processor defaults a missing "enabled" to true, so the column does too.
    private static bool EnabledOf(JsonElement rule)
    {
        return !rule.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean();
    }

    private static JsonElement ToJson(string body)
    {
        using var document = JsonDocument.Parse(body);

        return document.RootElement.Clone();
    }
}
