using Notifier.Domain.Models;

namespace Notifier.Domain.Repositories;

// One repository for all read-only copies fed by config.changes: they are written by one consumer
// and read together when recipients are resolved.
public interface IConfigRepository
{
    Task<Rule?> FindRuleAsync(string id, CancellationToken cancellationToken = default);

    Task<Team?> FindTeamAsync(string id, CancellationToken cancellationToken = default);

    Task<User?> FindUserAsync(string id, CancellationToken cancellationToken = default);

    Task<Membership?> FindMembershipAsync(string teamId, string userId, CancellationToken cancellationToken = default);

    Task<UserPreferences?> FindPreferencesAsync(string userId, CancellationToken cancellationToken = default);

    Task<Runbook?> FindRunbookAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Membership>> GetMembershipsAsync(
        IReadOnlyCollection<string> teamIds,
        IReadOnlyCollection<string> userIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<User>> GetUsersAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UserPreferences>> GetPreferencesAsync(IReadOnlyCollection<string> userIds, CancellationToken cancellationToken = default);

    Task AddAsync(object copy, CancellationToken cancellationToken = default);

    void Remove(Membership membership);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
