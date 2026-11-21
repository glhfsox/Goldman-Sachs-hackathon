using Ingest.Application.DTOs.Requests;

namespace Ingest.Application.Interfaces;

public interface ISourceService
{
    // Returns false when the change was not newer than the stored row and was ignored.
    Task<bool> ApplyChangeAsync(ApplySourceChangeRequest request, CancellationToken cancellationToken = default);
}
