using AlertApi.Application.DTOs.Requests;
using AlertApi.Application.DTOs.Responses;

namespace AlertApi.Application.Interfaces;

public interface ISourceService
{
    Task<IReadOnlyList<SourceResponse>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<SourceResponse> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<SourceResponse> CreateAsync(CreateSourceRequest request, CancellationToken cancellationToken = default);

    Task<SourceResponse> UpdateAsync(string id, UpdateSourceRequest request, CancellationToken cancellationToken = default);
}
