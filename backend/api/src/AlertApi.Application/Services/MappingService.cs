using System.Text.Json;

using AlertApi.Application.Exceptions;
using AlertApi.Application.Interfaces;
using AlertApi.Application.Validation;
using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;

namespace AlertApi.Application.Services;

public sealed class MappingService : IMappingService
{
    private readonly IMappingRepository _repository;
    private readonly ISchemaSampleRepository _schemaSamples;
    private readonly IConfigChangeOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public MappingService(
        IMappingRepository repository,
        ISchemaSampleRepository schemaSamples,
        IConfigChangeOutbox outbox,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _schemaSamples = schemaSamples;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<JsonElement>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var mappings = await _repository.GetAllAsync(cancellationToken);

        return mappings.Select(mapping => ToJson(mapping.Body)).ToList();
    }

    public async Task<JsonElement> SaveAsync(
        string id,
        JsonElement mapping,
        string? fingerprint,
        CancellationToken cancellationToken = default)
    {
        var problems = MappingValidator.Validate(mapping);

        if (problems.Count == 0 && mapping.GetProperty("id").GetString() != id)
        {
            problems.Add(new ProblemItem("id", "must match the id in the URL"));
        }

        if (problems.Count > 0)
        {
            throw new InvalidMappingException(problems);
        }

        // Looked up before any write, so an unknown fingerprint saves nothing.
        var sample = fingerprint is null
            ? null
            : await _schemaSamples.GetByFingerprintAsync(fingerprint, cancellationToken)
                ?? throw new SchemaSampleNotFoundException(fingerprint);

        var status = mapping.GetProperty("status").GetString()!;
        var origin = mapping.GetProperty("origin").GetString()!;
        var priority = mapping.GetProperty("priority").GetInt32();
        var now = _timeProvider.GetUtcNow();
        var existing = await _repository.GetByIdAsync(id, cancellationToken);

        if (existing is null)
        {
            existing = Mapping.Create(id, status, origin, priority, mapping.GetRawText(), now);
            await _repository.AddAsync(existing, cancellationToken);
        }
        else
        {
            existing.Update(status, origin, priority, mapping.GetRawText(), now);
        }

        sample?.MarkMapped();
        _outbox.Add("mapping", id, now, mapping);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ToJson(existing.Body);
    }

    private static JsonElement ToJson(string body)
    {
        using var document = JsonDocument.Parse(body);

        return document.RootElement.Clone();
    }
}
