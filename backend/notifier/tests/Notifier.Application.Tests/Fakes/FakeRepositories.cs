using Notifier.Domain.Models;
using Notifier.Domain.Repositories;
using Notifier.Domain.ValueObjects;

namespace Notifier.Application.Tests.Fakes;

public sealed class FakeRepositories :
    INotificationRepository,
    IAlertRepository,
    IConfigRepository,
    IRateCounterRepository,
    IOutboxRepository,
    IProcessedChangeRepository
{
    private readonly InMemoryStore _store;

    public FakeRepositories(InMemoryStore store)
    {
        _store = store;
    }

    public async Task<bool> ExistsAsync(string id, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        return _store.Notifications.Any(notification => notification.Id == id);
    }

    public async Task AddAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        _store.Notifications.Add(notification);
    }

    public async Task<IReadOnlyList<Notification>> GetOpenByAlertAsync(string alertId, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        return _store.Notifications.Where(notification => notification.AlertId == alertId && notification.IsOpen).ToList();
    }

    public async Task<bool> HasDeliveredToGroupAsync(
        string userId,
        string groupKey,
        string excludedAlertId,
        DateTime since,
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        return _store.Notifications.Any(notification => notification.UserId == userId
            && notification.GroupKey == groupKey
            && notification.AlertId != excludedAlertId
            && notification.Channel != "ui"
            && notification.CreatedAt >= since
            && notification.Status is NotificationStatus.Pending or NotificationStatus.Sent or NotificationStatus.Failed);
    }

    public async Task<IReadOnlyList<Notification>> ClaimDueAsync(
        DateTime now,
        int batchSize,
        DateTime leaseUntil,
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        return _store.Notifications
            .Where(notification => notification.IsOpen && notification.SendAt <= now)
            .OrderBy(notification => notification.SendAt)
            .Take(batchSize)
            .ToList();
    }

    public async Task<Alert?> FindAsync(string id, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        return _store.Alerts.FirstOrDefault(alert => alert.Id == id);
    }

    public async Task AddAsync(Alert alert, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        _store.Alerts.Add(alert);
    }

    public async Task<Rule?> FindRuleAsync(string id, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        return _store.Copies<Rule>().FirstOrDefault(rule => rule.Id == id);
    }

    public async Task<Team?> FindTeamAsync(string id, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        return _store.Copies<Team>().FirstOrDefault(team => team.Id == id);
    }

    public async Task<User?> FindUserAsync(string id, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        return _store.Copies<User>().FirstOrDefault(user => user.Id == id);
    }

    public async Task<Membership?> FindMembershipAsync(string teamId, string userId, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        return _store.Copies<Membership>().FirstOrDefault(membership => membership.TeamId == teamId && membership.UserId == userId);
    }

    public async Task<UserPreferences?> FindPreferencesAsync(string userId, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        return _store.Copies<UserPreferences>().FirstOrDefault(preferences => preferences.UserId == userId);
    }

    public async Task<Runbook?> FindRunbookAsync(string id, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        return _store.Copies<Runbook>().FirstOrDefault(runbook => runbook.Id == id);
    }

    public async Task<IReadOnlyList<Membership>> GetMembershipsAsync(
        IReadOnlyCollection<string> teamIds,
        IReadOnlyCollection<string> userIds,
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        return _store.Copies<Membership>()
            .Where(membership => teamIds.Contains(membership.TeamId) || userIds.Contains(membership.UserId))
            .ToList();
    }

    public async Task<IReadOnlyList<User>> GetUsersAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        return _store.Copies<User>().Where(user => ids.Contains(user.Id)).ToList();
    }

    public async Task<IReadOnlyList<UserPreferences>> GetPreferencesAsync(
        IReadOnlyCollection<string> userIds,
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        return _store.Copies<UserPreferences>().Where(preferences => userIds.Contains(preferences.UserId)).ToList();
    }

    async Task IConfigRepository.AddAsync(object copy, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        _store.Config.Add(copy);
    }

    public void Remove(Membership membership)
    {
        _store.Config.Remove(membership);
    }

    async Task<RateCounter?> IRateCounterRepository.FindAsync(string userId, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;

        return _store.RateCounters.FirstOrDefault(counter => counter.UserId == userId);
    }

    public async Task AddAsync(RateCounter counter, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        _store.RateCounters.Add(counter);
    }

    public async Task AddAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        _store.Outbox.Add(message);
    }

    public async Task<IReadOnlyList<OutboxMessage>> ClaimPendingAsync(
        DateTime now,
        int batchSize,
        DateTime leaseUntil,
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        return _store.Outbox.Where(message => message.ProcessedAt is null).Take(batchSize).ToList();
    }

    async Task<bool> IProcessedChangeRepository.ExistsAsync(string changeId, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;

        return _store.ProcessedChanges.Any(change => change.ChangeId == changeId);
    }

    public async Task AddAsync(ProcessedChange change, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        _store.ProcessedChanges.Add(change);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
    }
}
