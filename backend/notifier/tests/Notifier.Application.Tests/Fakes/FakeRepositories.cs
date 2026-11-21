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

    public int LoadAllCalls { get; private set; }

    public FakeRepositories(InMemoryStore store)
    {
        _store = store;
    }

    async Task<Notification?> INotificationRepository.FindAsync(string id, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;

        return _store.Notifications.FirstOrDefault(notification => notification.Id == id);
    }

    public async Task<IReadOnlySet<string>> GetExistingIdsAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        return _store.Notifications.Select(notification => notification.Id).Where(ids.Contains).ToHashSet();
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

    public async Task<IReadOnlyList<Notification>> GetOpenByAlertsAsync(IReadOnlyCollection<string> alertIds, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        return _store.Notifications.Where(notification => alertIds.Contains(notification.AlertId) && notification.IsOpen).ToList();
    }

    public Task LockGroupsAsync(IReadOnlyCollection<string> groupKeys, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<IReadOnlyList<NotifiedEntry>> GetNotifiedAsync(
        IReadOnlyCollection<string> userIds,
        IReadOnlyCollection<string> groupKeys,
        DateTime since,
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        return _store.Notifications.Where(notification => userIds.Contains(notification.UserId)
            && groupKeys.Contains(notification.GroupKey)
            && notification.Channel != "ui"
            && notification.CreatedAt >= since
            && notification.Status is NotificationStatus.Pending or NotificationStatus.Sent or NotificationStatus.Failed)
            .Select(notification => new NotifiedEntry(notification.GroupKey, notification.UserId, notification.AlertId, notification.CreatedAt))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> ClaimDueAsync(
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
            .Select(notification => notification.Id)
            .ToList();
    }

    public async Task<int> DeleteFinishedBeforeAsync(DateTime before, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        return _store.Notifications.RemoveAll(notification => !notification.IsOpen && notification.UpdatedAt < before);
    }

    public async Task<IReadOnlyList<Alert>> GetByIdsAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        return _store.Alerts.Where(alert => ids.Contains(alert.Id)).ToList();
    }

    public async Task<AlertStatus?> GetStatusAsync(string id, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        return _store.Alerts.FirstOrDefault(alert => alert.Id == id)?.Status;
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

    public async Task<ConfigCopies> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        LoadAllCalls++;

        return new ConfigCopies(
            _store.Copies<Rule>().ToList(),
            _store.Copies<User>().ToList(),
            _store.Copies<Membership>().ToList(),
            _store.Copies<UserPreferences>().ToList(),
            _store.Copies<Runbook>().ToList());
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

    public async Task<RateCounter> LockAsync(string userId, DateTime now, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        var counter = _store.RateCounters.FirstOrDefault(existing => existing.UserId == userId);
        if (counter is null)
        {
            counter = RateCounter.Create(userId, now);
            _store.RateCounters.Add(counter);
        }

        return counter;
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

    public async Task<int> DeleteProcessedBeforeAsync(DateTime before, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        return _store.Outbox.RemoveAll(message => message.ProcessedAt is { } processedAt && processedAt < before);
    }

    public async Task<int> DeleteBeforeAsync(DateTime before, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        return _store.ProcessedChanges.RemoveAll(change => change.ProcessedAt < before);
    }

    public async Task<IReadOnlySet<string>> GetExistingAsync(IReadOnlyCollection<string> changeIds, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        return _store.ProcessedChanges.Select(change => change.ChangeId).Where(changeIds.Contains).ToHashSet();
    }

    public async Task AddAsync(ProcessedChange change, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        _store.ProcessedChanges.Add(change);
    }

    public int SaveCalls { get; private set; }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        SaveCalls++;
    }
}
