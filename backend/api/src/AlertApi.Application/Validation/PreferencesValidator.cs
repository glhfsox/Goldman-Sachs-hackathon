using System.Text.Json;
using System.Text.RegularExpressions;

using AlertApi.Application.Exceptions;

namespace AlertApi.Application.Validation;

// Channel preferences of one person (contracts/rules.md section 5).
public static partial class PreferencesValidator
{
    private static readonly string[] Channels = ["ui", "email", "sms", "call"];

    public static List<ProblemItem> Validate(JsonElement preferences)
    {
        var problems = new List<ProblemItem>();

        if (!JsonChecks.RequireObject(
                preferences, string.Empty, problems,
                "user_id", "timezone", "by_severity", "quiet_hours", "wake_me", "max_push_per_hour"))
        {
            return problems;
        }

        if (JsonChecks.TryGetRequired(preferences, string.Empty, "user_id", problems, out var userId))
        {
            JsonChecks.CheckString(userId, "user_id", problems);
        }

        if (preferences.TryGetProperty("timezone", out var timezone))
        {
            JsonChecks.CheckString(timezone, "timezone", problems);
        }

        if (preferences.TryGetProperty("by_severity", out var bySeverity))
        {
            ValidateBySeverity(bySeverity, problems);
        }

        if (preferences.TryGetProperty("quiet_hours", out var quietHours))
        {
            ValidateQuietHours(quietHours, problems);
        }

        if (preferences.TryGetProperty("wake_me", out var wakeMe))
        {
            JsonChecks.CheckBool(wakeMe, "wake_me", problems);
        }

        if (preferences.TryGetProperty("max_push_per_hour", out var limit) && !(JsonChecks.IsWholeNumber(limit, out var perHour) && perHour > 0))
        {
            problems.Add(new ProblemItem("max_push_per_hour", "must be a whole number above 0"));
        }

        return problems;
    }

    private static void ValidateBySeverity(JsonElement bySeverity, List<ProblemItem> problems)
    {
        if (!JsonChecks.RequireObject(bySeverity, "by_severity", problems, JsonChecks.Severities))
        {
            return;
        }

        foreach (var entry in bySeverity.EnumerateObject().Where(entry => JsonChecks.Severities.Contains(entry.Name)))
        {
            var path = JsonChecks.Join("by_severity", entry.Name);

            if (entry.Value.ValueKind != JsonValueKind.Array)
            {
                problems.Add(new ProblemItem(path, "must be an array of channels"));

                continue;
            }

            var index = 0;

            foreach (var channel in entry.Value.EnumerateArray())
            {
                JsonChecks.CheckOneOf(channel, $"{path}[{index++}]", problems, Channels);
            }
        }
    }

    private static void ValidateQuietHours(JsonElement quietHours, List<ProblemItem> problems)
    {
        if (!JsonChecks.RequireObject(quietHours, "quiet_hours", problems, "start", "end", "downgrade"))
        {
            return;
        }

        foreach (var name in new[] { "start", "end" })
        {
            var path = JsonChecks.Join("quiet_hours", name);

            if (!JsonChecks.TryGetRequired(quietHours, "quiet_hours", name, problems, out var time))
            {
                continue;
            }

            if (time.ValueKind != JsonValueKind.String || !ClockPattern().IsMatch(time.GetString()!))
            {
                problems.Add(new ProblemItem(path, "must be a time like '08:00'"));
            }
        }

        if (!quietHours.TryGetProperty("downgrade", out var downgrade)
            || !JsonChecks.RequireObject(downgrade, "quiet_hours.downgrade", problems, Channels))
        {
            return;
        }

        foreach (var entry in downgrade.EnumerateObject().Where(entry => Channels.Contains(entry.Name)))
        {
            JsonChecks.CheckOneOf(entry.Value, JsonChecks.Join("quiet_hours.downgrade", entry.Name), problems, Channels);
        }
    }

    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d$")]
    private static partial Regex ClockPattern();
}
