using AlertApi.Application.DTOs.Messages;
using AlertApi.Application.DTOs.Requests;
using AlertApi.Application.DTOs.Responses;
using AlertApi.Application.Exceptions;
using AlertApi.Application.Interfaces;
using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;

namespace AlertApi.Application.Services;

public sealed class SchemaSampleService : ISchemaSampleService
{
    private readonly ISchemaSampleRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public SchemaSampleService(ISchemaSampleRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<IReadOnlyList<SchemaSampleResponse>> GetAllAsync(
        string? status,
        CancellationToken cancellationToken = default)
    {
        var samples = await _repository.GetAllAsync(status, cancellationToken);

        return samples.Select(SchemaSampleResponse.From).ToList();
    }

    public async Task<SchemaSampleResponse> MarkFailedAsync(
        string fingerprint,
        FailSchemaSampleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Status != SchemaSample.Failed)
        {
            throw new InvalidSchemaSampleException([new ProblemItem("status", "must be 'failed'")]);
        }

        var sample = await _repository.GetByFingerprintAsync(fingerprint, cancellationToken)
            ?? throw new SchemaSampleNotFoundException(fingerprint);

        if (sample.Status == SchemaSample.Mapped)
        {
            throw new SchemaSampleAlreadyMappedException(fingerprint);
        }

        sample.MarkFailed(request.Error);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return SchemaSampleResponse.From(sample);
    }

    public async Task ApplySampleAsync(SchemaSampleMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // The processor publishes one record per fingerprint; a repeat must not undo a later mapped or failed status.
        if (await _repository.GetByFingerprintAsync(message.Fingerprint, cancellationToken) is not null)
        {
            return;
        }

        await _repository.AddAsync(
            SchemaSample.Create(message.Fingerprint, message.SourceId, message.Sample.GetRawText(), message.SeenAt),
            cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
