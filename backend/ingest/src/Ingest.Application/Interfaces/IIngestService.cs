using Ingest.Application.DTOs.Responses;

namespace Ingest.Application.Interfaces;

public interface IIngestService
{
    Task<IngestResponse> IngestHttpAsync(
        string sourceId,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default);
}
