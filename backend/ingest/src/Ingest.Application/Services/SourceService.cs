using Ingest.Application.DTOs.Requests;
using Ingest.Application.Interfaces;
using Ingest.Domain.Models;
using Ingest.Domain.Repositories;

namespace Ingest.Application.Services;

public sealed class SourceService : ISourceService
{
    private readonly ISourceRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public SourceService(
        ISourceRepository repository,
        IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<bool> ApplyChangeAsync(
        ApplySourceChangeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var source = await _repository.GetByIdAsync(request.Id, cancellationToken);

        if (source is null)
        {
            await _repository.AddAsync(
                Source.Create(request.Id, request.Kind, request.Config, request.Enabled, request.UpdatedAt),
                cancellationToken);
        }
        else if (!source.ApplyChange(request.Kind, request.Config, request.Enabled, request.UpdatedAt))
        {
            return false;
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return true;
    }
}
