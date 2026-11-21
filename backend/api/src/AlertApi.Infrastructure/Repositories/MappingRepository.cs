using Microsoft.EntityFrameworkCore;

using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;
using AlertApi.Infrastructure.Data;

namespace AlertApi.Infrastructure.Repositories;

public sealed class MappingRepository : IMappingRepository
{
    private readonly ApiDbContext _context;

    public MappingRepository(ApiDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<Mapping>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Mappings
            .AsNoTracking()
            .OrderByDescending(mapping => mapping.Priority)
            .ThenBy(mapping => mapping.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<Mapping?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _context.Mappings.FirstOrDefaultAsync(mapping => mapping.Id == id, cancellationToken);
    }

    public async Task AddAsync(Mapping mapping, CancellationToken cancellationToken = default)
    {
        await _context.Mappings.AddAsync(mapping, cancellationToken);
    }
}
