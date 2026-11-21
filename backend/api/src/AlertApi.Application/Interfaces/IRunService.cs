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

    // The ai-worker's verdict on a scheduled `auto` run. `hold` sends it to a person; `clear` lets it start at its start time.
    Task<RunResponse> RiskCheckAsync(string id, RiskCheckRequest request, string? userId, CancellationToken cancellationToken = default);

    // A person approves a held run; it starts at once.
    Task<RunResponse> ApproveAsync(string id, string? userId, CancellationToken cancellationToken = default);

    // Runs the ai-worker polls: `pending` (scheduled, auto, no verdict yet) or finished since a time.
    Task<IReadOnlyList<RunResponse>> ListAsync(string? riskCheck, DateTimeOffset? finishedSince, int? limit, CancellationToken cancellationToken = default);
}
