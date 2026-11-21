using Microsoft.EntityFrameworkCore;

using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;
using AlertApi.Infrastructure.Data;

namespace AlertApi.Infrastructure.Repositories;

public sealed class PreferencesRepository : IPreferencesRepository
{
    private readonly ApiDbContext _context;

    public PreferencesRepository(ApiDbContext context)
    {
        _context = context;
    }

    public async Task<UserPreferences?> GetByUserIdAsync(string userId, CancellationToken cancellationToken = default)
    {
        return await _context.Preferences.FirstOrDefaultAsync(preferences => preferences.UserId == userId, cancellationToken);
    }

    public async Task AddAsync(UserPreferences preferences, CancellationToken cancellationToken = default)
    {
        await _context.Preferences.AddAsync(preferences, cancellationToken);
    }
}
