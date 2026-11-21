using System.Text.Json;

using AlertApi.Application.Exceptions;
using AlertApi.Application.Interfaces;
using AlertApi.Application.Validation;
using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;

namespace AlertApi.Application.Services;

public sealed class PreferencesService : IPreferencesService
{
    private readonly IPreferencesRepository _repository;
    private readonly IReferenceDataQueries _queries;
    private readonly IConfigChangeOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public PreferencesService(
        IPreferencesRepository repository,
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

    public async Task<JsonElement> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        await EnsureUserExistsAsync(userId, cancellationToken);

        var preferences = await _repository.GetByUserIdAsync(userId, cancellationToken)
            ?? throw new PreferencesNotFoundException(userId);

        return ToJson(preferences.Body);
    }

    public async Task<JsonElement> SaveAsync(string userId, JsonElement preferences, CancellationToken cancellationToken = default)
    {
        await EnsureUserExistsAsync(userId, cancellationToken);

        var problems = PreferencesValidator.Validate(preferences);

        if (problems.Count == 0 && preferences.GetProperty("user_id").GetString() != userId)
        {
            problems.Add(new ProblemItem("user_id", "must match the user id in the URL"));
        }

        if (problems.Count > 0)
        {
            throw new InvalidPreferencesException(problems);
        }

        var existing = await _repository.GetByUserIdAsync(userId, cancellationToken);

        if (existing is null)
        {
            existing = UserPreferences.Create(userId, preferences.GetRawText());
            await _repository.AddAsync(existing, cancellationToken);
        }
        else
        {
            existing.Replace(preferences.GetRawText());
        }

        _outbox.Add("preferences", userId, _timeProvider.GetUtcNow(), preferences);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ToJson(existing.Body);
    }

    private async Task EnsureUserExistsAsync(string userId, CancellationToken cancellationToken)
    {
        if (!await _queries.UserExistsAsync(userId, cancellationToken))
        {
            throw new UserNotFoundException(userId);
        }
    }

    private static JsonElement ToJson(string body)
    {
        using var document = JsonDocument.Parse(body);

        return document.RootElement.Clone();
    }
}
