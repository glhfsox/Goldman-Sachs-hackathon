using Microsoft.EntityFrameworkCore;

using Ingest.Domain.Models;
using Ingest.Domain.Repositories;
using Ingest.Infrastructure.Data;

namespace Ingest.Infrastructure.Repositories;

public sealed class SourceRepository : ISourceRepository
{
    private readonly IngestDbContext _context;

    public SourceRepository(IngestDbContext context)
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
