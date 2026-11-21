using System.Text.Json;

using AlertApi.Application.DTOs.Requests;
using AlertApi.Application.DTOs.Responses;

namespace AlertApi.Application.Interfaces;

public interface IRunbookService
{
    Task<IReadOnlyList<RunbookResponse>> GetAllAsync(string? teamId, CancellationToken cancellationToken = default);

    Task<RunbookResponse> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<RunbookResponse> CreateAsync(CreateRunbookRequest request, CancellationToken cancellationToken = default);

    Task<RunbookResponse> UpdateAsync(string id, UpdateRunbookRequest request, CancellationToken cancellationToken = default);

    Task<RunbookResponse> AcceptAsync(string id, string? userId, int? version, CancellationToken cancellationToken = default);

    // For an accepted proposal: a runbook that is active and accepted from the start, saved and published once.
    Task<RunbookResponse> CreateAcceptedAsync(string teamId, string title, JsonElement canonical, string userId, CancellationToken cancellationToken = default);
}
