using AlertApi.Application.DTOs.Responses;

namespace AlertApi.Application.Interfaces;

// Read side for the pickers: projects straight to responses and never changes state.
public interface IReferenceDataQueries
{
    Task<IReadOnlyList<TeamResponse>> GetTeamsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UserResponse>> GetUsersAsync(CancellationToken cancellationToken = default);

    Task<bool> UserExistsAsync(string userId, CancellationToken cancellationToken = default);
}
