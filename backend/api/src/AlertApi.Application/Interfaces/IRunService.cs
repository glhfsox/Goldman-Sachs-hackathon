using AlertApi.Application.DTOs.Requests;
using AlertApi.Application.DTOs.Responses;

namespace AlertApi.Application.Interfaces;

public interface IRunService
{
    Task<RunPreviewResponse> PreviewAsync(string alertId, string? runbookId, CancellationToken cancellationToken = default);

    // A person starts a run by hand; that counts as the approval of what the preview showed.
    Task<RunResponse> StartAsync(string alertId, StartRunRequest request, string? userId, CancellationToken cancellationToken = default);

    Task<RunResponse> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<RunResponse> CancelAsync(string id, string? userId, CancellationToken cancellationToken = default);

    // A person ticks a text-only step.
    Task<RunNodeResponse> TickAsync(string id, string node, string? userId, CancellationToken cancellationToken = default);
}
