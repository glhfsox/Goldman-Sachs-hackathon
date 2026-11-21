using AlertApi.Application.DTOs.Responses;

namespace AlertApi.Application.Interfaces;

public interface IAlertService
{
    Task<IReadOnlyList<AlertResponse>> GetAllAsync(
        string? teamId,
        string? status,
        int limit,
        CancellationToken cancellationToken = default);

    Task<AlertDetailResponse> GetByIdAsync(string id, CancellationToken cancellationToken = default);
}
