using Microsoft.EntityFrameworkCore;

using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;
using AlertApi.Infrastructure.Data;

namespace AlertApi.Infrastructure.Repositories;

public sealed class ProposalRepository : IProposalRepository
{
    private readonly ApiDbContext _context;

    public ProposalRepository(ApiDbContext context)
    {
        _context = context;
    }

    public async Task<Proposal?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _context.Proposals.FirstOrDefaultAsync(proposal => proposal.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<Proposal>> GetAllAsync(string? status, int limit, CancellationToken cancellationToken = default)
    {
        var query = _context.Proposals.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(proposal => proposal.Status == status);
        }

        return await query
            .OrderByDescending(proposal => proposal.CreatedAt)
            .ThenBy(proposal => proposal.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Proposal>> GetOpenAsync(string alertId, string kind, CancellationToken cancellationToken = default)
    {
        return await _context.Proposals
            .AsNoTracking()
            .Where(proposal => proposal.AlertId == alertId && proposal.Kind == kind && proposal.Status == Proposal.Open)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(Proposal proposal, CancellationToken cancellationToken = default)
    {
        await _context.Proposals.AddAsync(proposal, cancellationToken);
    }
}
