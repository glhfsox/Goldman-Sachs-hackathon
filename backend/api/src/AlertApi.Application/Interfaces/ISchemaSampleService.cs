using AlertApi.Application.DTOs.Messages;
using AlertApi.Application.DTOs.Requests;
using AlertApi.Application.DTOs.Responses;

namespace AlertApi.Application.Interfaces;

public interface ISchemaSampleService
{
    Task<IReadOnlyList<SchemaSampleResponse>> GetAllAsync(string? status, CancellationToken cancellationToken = default);

    Task<SchemaSampleResponse> MarkFailedAsync(
        string fingerprint,
        FailSchemaSampleRequest request,
        CancellationToken cancellationToken = default);

    Task ApplySampleAsync(SchemaSampleMessage message, CancellationToken cancellationToken = default);
}
