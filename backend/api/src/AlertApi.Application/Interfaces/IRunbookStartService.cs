using AlertApi.Application.DTOs.Requests;
using AlertApi.Application.DTOs.Responses;
using AlertApi.Domain.Models;

namespace AlertApi.Application.Interfaces;

public interface IRunbookStartService
{
    Task<IReadOnlyList<RunbookStartResponse>> GetAllAsync(string? ruleId, string? runbookId, CancellationToken cancellationToken = default);

    Task<RunbookStartResponse> PutAsync(string ruleId, string runbookId, PutRunbookStartRequest request, string? userId, CancellationToken cancellationToken = default);

    // Tracks the row without saving, so an accepted proposal commits it together with its own decision.
    Task<RunbookStart> StageAsync(string ruleId, string runbookId, string start, string userId, CancellationToken cancellationToken = default);
}
