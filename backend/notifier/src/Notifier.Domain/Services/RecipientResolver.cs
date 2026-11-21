using Notifier.Domain.Models;
using Notifier.Domain.ValueObjects;

namespace Notifier.Domain.Services;

// rule route -> team, then members or on-call, then personal preferences (OVERVIEW, "How one event flows" step 7).
public sealed class RecipientResolver
{
    public IReadOnlyList<Recipient> Resolve(
        string ruleId,
        RouteSpec route,
        Severity severity,
        IReadOnlyList<Membership> memberships,
        IReadOnlyDictionary<string, User> users,
        IReadOnlyDictionary<string, PreferenceSettings> preferences)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(severity);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(preferences);

        // A person reached through several route entries is still one recipient with every reason listed.
        var picked = new Dictionary<string, (bool OnCall, List<string> Reasons)>();
        foreach (var spec in route.Recipients)
        {
            if (spec.UserId is not null)
            {
                var onCall = memberships.Any(membership => membership.UserId == spec.UserId && membership.OnCall);
                Pick(picked, spec.UserId, onCall, "named in the rule");
                continue;
            }

            var members = memberships.Where(membership => membership.TeamId == spec.TeamId
                && (!spec.OnCallOnly || membership.OnCall));
            foreach (var member in members)
            {
                Pick(picked, member.UserId, member.OnCall, member.OnCall ? $"team {spec.TeamId}, on-call" : $"team {spec.TeamId}");
            }
        }

        var recipients = new List<Recipient>();
        foreach (var (userId, pick) in picked)
        {
            if (!users.TryGetValue(userId, out var user))
            {
                continue;
            }

            preferences.TryGetValue(userId, out var settings);
            recipients.Add(new Recipient(
                user.Id,
                user.Name,
                user.Email,
                user.Phone,
                pick.OnCall,
                ChannelsFor(route, severity, settings),
                $"rule {ruleId}, {string.Join("; ", pick.Reasons)}",
                settings?.TimeZone,
                settings?.QuietHours,
                settings?.WakeMe ?? false,
                settings?.MaxPushPerHour));
        }

        return recipients;
    }

    // route.channels wins; otherwise the person's by_severity; otherwise the UI only (rules.md §4).
    private static IReadOnlyList<string> ChannelsFor(RouteSpec route, Severity severity, PreferenceSettings? settings)
    {
        if (route.Channels is not null)
        {
            return route.Channels;
        }

        if (settings is not null && settings.BySeverity.TryGetValue(severity.Name, out var channels) && channels.Count > 0)
        {
            return channels;
        }

        return [Channel.Ui.Name];
    }

    private static void Pick(Dictionary<string, (bool OnCall, List<string> Reasons)> picked, string userId, bool onCall, string reason)
    {
        if (picked.TryGetValue(userId, out var existing))
        {
            existing.Reasons.Add(reason);
            picked[userId] = (existing.OnCall || onCall, existing.Reasons);

            return;
        }

        picked[userId] = (onCall, [reason]);
    }
}
