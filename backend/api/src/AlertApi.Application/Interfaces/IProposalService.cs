using AlertApi.Application.DTOs.Requests;
using AlertApi.Application.DTOs.Responses;

namespace AlertApi.Application.Interfaces;

public interface IProposalService
{
    Task<CreatedProposal> CreateAsync(string alertId, CreateProposalRequest request, string? userId, string? idempotencyKey, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProposalResponse>> GetAllAsync(string? status, int limit, CancellationToken cancellationToken = default);

    Task<ProposalResponse> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<ProposalResponse> AcceptAsync(string id, string? userId, CancellationToken cancellationToken = default);

    Task<ProposalResponse> RejectAsync(string id, string? userId, CancellationToken cancellationToken = default);
}
