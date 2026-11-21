using System.Text.Json;

using Notifier.Application.DTOs.Config;
using Notifier.Application.Serialization;
using Notifier.Domain.Models;
using Notifier.Domain.Repositories;
using Notifier.Domain.ValueObjects;

namespace Notifier.Application.Services;

public sealed record RuleCopy(Rule Rule, RuleBodyDto? Body);

public sealed record RunbookCopy(Runbook Runbook, CanonicalRunbookDto? Canonical);

// The configuration copies compiled for planning: JSON bodies parsed once per load, not once per alert.
public sealed class ConfigSnapshot
{
    private ConfigSnapshot(
        long version,
        DateTime loadedAt,
        IReadOnlyDictionary<string, RuleCopy> rules,
        IReadOnlyDictionary<string, User> users,
        IReadOnlyList<Membership> memberships,
        IReadOnlyDictionary<string, PreferenceSettings> preferences,
        IReadOnlyDictionary<string, RunbookCopy> runbooks)
    {
        Version = version;
        LoadedAt = loadedAt;
        Rules = rules;
        Users = users;
        Memberships = memberships;
        Preferences = preferences;
        Runbooks = runbooks;
    }

    public long Version { get; }

    public DateTime LoadedAt { get; }

    public IReadOnlyDictionary<string, RuleCopy> Rules { get; }

    public IReadOnlyDictionary<string, User> Users { get; }

    public IReadOnlyList<Membership> Memberships { get; }

    public IReadOnlyDictionary<string, PreferenceSettings> Preferences { get; }

    public IReadOnlyDictionary<string, RunbookCopy> Runbooks { get; }

    public static ConfigSnapshot Build(ConfigCopies copies, long version, DateTime loadedAt)
    {
        ArgumentNullException.ThrowIfNull(copies);

        return new ConfigSnapshot(
            version,
            loadedAt,
            copies.Rules.ToDictionary(
                rule => rule.Id,
                rule => new RuleCopy(rule, JsonSerializer.Deserialize<RuleBodyDto>(rule.Body, NotifierJson.Options))),
            copies.Users.ToDictionary(user => user.Id),
            copies.Memberships,
            copies.Preferences.ToDictionary(preferences => preferences.UserId, preferences => ToSettings(preferences.Body)),
            copies.Runbooks.ToDictionary(
                runbook => runbook.Id,
                runbook => new RunbookCopy(
                    runbook,
                    runbook.Canonical is null ? null : JsonSerializer.Deserialize<CanonicalRunbookDto>(runbook.Canonical, NotifierJson.Options))));
    }

    public RunbookCopy? FindActiveRunbook(string? runbookId)
    {
        if (string.IsNullOrWhiteSpace(runbookId) || !Runbooks.TryGetValue(runbookId, out var runbook))
        {
            return null;
        }

        return runbook.Runbook.IsActive ? runbook : null;
    }

    private static PreferenceSettings ToSettings(string body)
    {
        var dto = JsonSerializer.Deserialize<PreferencesDto>(body, NotifierJson.Options);
        QuietHours? quietHours = null;
        if (dto?.QuietHours is { } quiet
            && TimeOnly.TryParse(quiet.Start, out var start)
            && TimeOnly.TryParse(quiet.End, out var end))
        {
            quietHours = new QuietHours(start, end, quiet.Downgrade ?? []);
        }

        var bySeverity = (dto?.BySeverity ?? [])
            .ToDictionary(entry => entry.Key.ToLowerInvariant(), entry => (IReadOnlyList<string>)entry.Value);

        return new PreferenceSettings(dto?.Timezone, bySeverity, quietHours, dto?.WakeMe ?? false, dto?.MaxPushPerHour);
    }
}
