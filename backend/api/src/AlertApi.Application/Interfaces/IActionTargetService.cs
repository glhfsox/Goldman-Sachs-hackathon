using AlertApi.Application.DTOs.Requests;
using AlertApi.Application.DTOs.Responses;

namespace AlertApi.Application.Interfaces;

public interface IActionTargetService
{
    IReadOnlyList<ActionResponse> GetActions();

    Task<IReadOnlyList<ActionTargetResponse>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<ActionTargetResponse> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<ActionTargetResponse> PutAsync(string id, PutActionTargetRequest request, CancellationToken cancellationToken = default);
}
