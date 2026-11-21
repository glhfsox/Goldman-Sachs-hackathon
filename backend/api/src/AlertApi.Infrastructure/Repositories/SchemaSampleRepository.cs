using Microsoft.EntityFrameworkCore;

using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;
using AlertApi.Infrastructure.Data;

namespace AlertApi.Infrastructure.Repositories;

public sealed class SchemaSampleRepository : ISchemaSampleRepository
{
    private readonly ApiDbContext _context;

    public SchemaSampleRepository(ApiDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<SchemaSample>> GetAllAsync(string? status, CancellationToken cancellationToken = default)
    {
        var query = _context.SchemaSamples.AsNoTracking();

        if (status is not null)
        {
            query = query.Where(sample => sample.Status == status);
        }

        return await query
            .OrderBy(sample => sample.CreatedAt)
            .ThenBy(sample => sample.Fingerprint)
            .ToListAsync(cancellationToken);
    }

    public async Task<SchemaSample?> GetByFingerprintAsync(string fingerprint, CancellationToken cancellationToken = default)
    {
        return await _context.SchemaSamples.FirstOrDefaultAsync(sample => sample.Fingerprint == fingerprint, cancellationToken);
    }

    public async Task AddAsync(SchemaSample sample, CancellationToken cancellationToken = default)
    {
        await _context.SchemaSamples.AddAsync(sample, cancellationToken);
    }
}
