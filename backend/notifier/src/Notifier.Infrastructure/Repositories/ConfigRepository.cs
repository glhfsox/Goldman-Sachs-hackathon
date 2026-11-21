using Microsoft.EntityFrameworkCore;

using Notifier.Domain.Models;
using Notifier.Domain.Repositories;
using Notifier.Infrastructure.Data;

namespace Notifier.Infrastructure.Repositories;

public sealed class ConfigRepository : IConfigRepository
{
    private readonly NotifierDbContext _context;

    public ConfigRepository(NotifierDbContext context)
    {
        _context = context;
    }

    public async Task<Rule?> FindRuleAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _context.Rules.FirstOrDefaultAsync(rule => rule.Id == id, cancellationToken);
    }

    public async Task<Team?> FindTeamAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _context.Teams.FirstOrDefaultAsync(team => team.Id == id, cancellationToken);
    }

    public async Task<User?> FindUserAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _context.Users.FirstOrDefaultAsync(user => user.Id == id, cancellationToken);
    }

    public async Task<Membership?> FindMembershipAsync(string teamId, string userId, CancellationToken cancellationToken = default)
    {
        return await _context.Memberships
            .FirstOrDefaultAsync(membership => membership.TeamId == teamId && membership.UserId == userId, cancellationToken);
    }

    public async Task<UserPreferences?> FindPreferencesAsync(string userId, CancellationToken cancellationToken = default)
    {
        return await _context.Preferences.FirstOrDefaultAsync(preferences => preferences.UserId == userId, cancellationToken);
    }

    public async Task<Runbook?> FindRunbookAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _context.Runbooks.AsNoTracking().FirstOrDefaultAsync(runbook => runbook.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<Membership>> GetMembershipsAsync(
        IReadOnlyCollection<string> teamIds,
        IReadOnlyCollection<string> userIds,
        CancellationToken cancellationToken = default)
    {
        return await _context.Memberships.AsNoTracking()
            .Where(membership => teamIds.Contains(membership.TeamId) || userIds.Contains(membership.UserId))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<User>> GetUsersAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default)
    {
        return await _context.Users.AsNoTracking()
            .Where(user => ids.Contains(user.Id))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UserPreferences>> GetPreferencesAsync(
        IReadOnlyCollection<string> userIds,
        CancellationToken cancellationToken = default)
    {
        return await _context.Preferences.AsNoTracking()
            .Where(preferences => userIds.Contains(preferences.UserId))
            .ToListAsync(cancellationToken);
    }

    public async Task<ConfigCopies> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        var rules = await _context.Rules.AsNoTracking().ToListAsync(cancellationToken);
        var users = await _context.Users.AsNoTracking().ToListAsync(cancellationToken);
        var memberships = await _context.Memberships.AsNoTracking().ToListAsync(cancellationToken);
        var preferences = await _context.Preferences.AsNoTracking().ToListAsync(cancellationToken);
        var runbooks = await _context.Runbooks.AsNoTracking().ToListAsync(cancellationToken);

        return new ConfigCopies(rules, users, memberships, preferences, runbooks);
    }

    public async Task AddAsync(object copy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(copy);

        await _context.AddAsync(copy, cancellationToken);
    }

    public void Remove(Membership membership)
    {
        ArgumentNullException.ThrowIfNull(membership);

        _context.Memberships.Remove(membership);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await _context.SaveChangesAsync(cancellationToken);
    }
}
