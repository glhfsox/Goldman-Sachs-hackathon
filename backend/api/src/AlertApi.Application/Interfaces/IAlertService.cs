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

    // Publishes an acked record to alerts.changes; the alert copy changes when that record comes back.
    // The result is the alert as published, so the caller sees the new status at once.
    Task<AlertResponse> AckAsync(string id, string? userId, CancellationToken cancellationToken = default);

    Task<AlertResponse> ResolveAsync(string id, string? userId, CancellationToken cancellationToken = default);
}
