using Microsoft.EntityFrameworkCore;

using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;
using AlertApi.Infrastructure.Data;

namespace AlertApi.Infrastructure.Repositories;

public sealed class SourceRepository : ISourceRepository
{
    private readonly ApiDbContext _context;

    public SourceRepository(ApiDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<Source>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Sources
            .AsNoTracking()
            .OrderBy(source => source.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<Source?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _context.Sources
            .FirstOrDefaultAsync(source => source.Id == id, cancellationToken);
    }

    public async Task AddAsync(Source source, CancellationToken cancellationToken = default)
    {
        await _context.Sources.AddAsync(source, cancellationToken);
    }
}
