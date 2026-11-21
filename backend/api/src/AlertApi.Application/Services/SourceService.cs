using System.Text.RegularExpressions;

using AlertApi.Application.DTOs.Requests;
using AlertApi.Application.DTOs.Responses;
using AlertApi.Application.Exceptions;
using AlertApi.Application.Interfaces;
using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;
using AlertApi.Domain.ValueObjects;

namespace AlertApi.Application.Services;

public sealed partial class SourceService : ISourceService
{
    private readonly ISourceRepository _repository;
    private readonly IConfigChangeOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public SourceService(
        ISourceRepository repository,
        IConfigChangeOutbox outbox,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<SourceResponse>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var sources = await _repository.GetAllAsync(cancellationToken);

        return sources.Select(ToResponse).ToList();
    }

    public async Task<SourceResponse> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return ToResponse(await GetRequiredAsync(id, cancellationToken));
    }

    public async Task<SourceResponse> CreateAsync(
        CreateSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var problems = Validate(request.Kind, request.Config);

        if (string.IsNullOrWhiteSpace(request.Id) || !IdPattern().IsMatch(request.Id))
        {
            problems.Add(new ProblemItem("id", "must contain only lowercase letters, digits and '-'"));
        }

        if (problems.Count > 0)
        {
            throw new InvalidSourceException(problems);
        }

        await EnsureSourceIsUniqueAsync(request.Id, cancellationToken);

        var source = Source.Create(
            request.Id,
            ParseKind(request.Kind),
            ToConfig(request.Config),
            enabled: true,
            _timeProvider.GetUtcNow());

        await _repository.AddAsync(source, cancellationToken);
        Publish(source);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ToResponse(source);
    }

    public async Task<SourceResponse> UpdateAsync(
        string id,
        UpdateSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var source = await GetRequiredAsync(id, cancellationToken);
        var problems = Validate(request.Kind, request.Config);

        if (problems.Count > 0)
        {
            throw new InvalidSourceException(problems);
        }

        source.Update(ParseKind(request.Kind), ToConfig(request.Config), request.Enabled, _timeProvider.GetUtcNow());
        Publish(source);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ToResponse(source);
    }

    private async Task<Source> GetRequiredAsync(string id, CancellationToken cancellationToken)
    {
        return await _repository.GetByIdAsync(id, cancellationToken)
            ?? throw new SourceNotFoundException(id);
    }

    private async Task EnsureSourceIsUniqueAsync(string id, CancellationToken cancellationToken)
    {
        if (await _repository.GetByIdAsync(id, cancellationToken) is not null)
        {
            throw new SourceAlreadyExistsException(id);
        }
    }

    private void Publish(Source source)
    {
        var response = ToResponse(source);

        _outbox.Add("source", source.Id, source.UpdatedAt, new SourceBody(response.Id, response.Kind, response.Config, response.Enabled));
    }

    private static List<ProblemItem> Validate(string kind, SourceConfigDto? config)
    {
        var problems = new List<ProblemItem>();

        if (!Enum.TryParse<SourceKind>(kind, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
        {
            problems.Add(new ProblemItem("kind", "must be 'http' or 'kafka'"));
        }
        else if (parsed == SourceKind.Kafka && string.IsNullOrWhiteSpace(config?.Topic))
        {
            problems.Add(new ProblemItem("config.topic", "is required for a kafka source"));
        }

        return problems;
    }

    private static SourceKind ParseKind(string kind)
    {
        return Enum.Parse<SourceKind>(kind, ignoreCase: true);
    }

    private static SourceConfig ToConfig(SourceConfigDto? config)
    {
        return config is null ? SourceConfig.Empty : new SourceConfig(config.Topic, config.Brokers);
    }

    private static SourceResponse ToResponse(Source source)
    {
        return new SourceResponse(
            source.Id,
            source.Kind.ToString().ToLowerInvariant(),
            new SourceConfigDto(source.Config.Topic, source.Config.Brokers),
            source.Enabled,
            source.UpdatedAt);
    }

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex IdPattern();
}
