using System.Text.Json;

using AlertApi.Application.Exceptions;

namespace AlertApi.Application.Validation;

// A rule as the processor and the notifier read it (contracts/rules.md section 4).
public static class RuleValidator
{
    public static List<ProblemItem> Validate(JsonElement rule)
    {
        var problems = new List<ProblemItem>();

        if (!JsonChecks.RequireObject(
                rule, string.Empty, problems,
                "id", "team_id", "name", "enabled", "severity", "when", "alert_key", "window_s", "route", "present", "runbook_id"))
        {
            return problems;
        }

        foreach (var name in new[] { "id", "team_id" })
        {
            if (JsonChecks.TryGetRequired(rule, string.Empty, name, problems, out var value))
            {
                JsonChecks.CheckString(value, name, problems);
            }
        }

        foreach (var name in new[] { "name", "runbook_id" })
        {
            if (rule.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null)
            {
                JsonChecks.CheckString(value, name, problems);
            }
        }

        if (rule.TryGetProperty("enabled", out var enabled))
        {
            JsonChecks.CheckBool(enabled, "enabled", problems);
        }

        // Null means "the event's severity", the same as leaving the field out (contracts/rules.md section 4).
        if (rule.TryGetProperty("severity", out var severity) && severity.ValueKind != JsonValueKind.Null)
        {
            JsonChecks.CheckOneOf(severity, "severity", problems, "info", "warning", "error", "critical");
        }

        if (JsonChecks.TryGetRequired(rule, string.Empty, "when", problems, out var when))
        {
            ConditionValidator.Validate(when, "when", problems);
        }

        if (rule.TryGetProperty("alert_key", out var alertKey) && alertKey.ValueKind != JsonValueKind.Null)
        {
            JsonChecks.CheckPathList(alertKey, "alert_key", problems);
        }

        if (rule.TryGetProperty("window_s", out var window) && !(JsonChecks.IsWholeNumber(window, out var seconds) && seconds > 0))
        {
            problems.Add(new ProblemItem("window_s", "must be a whole number of seconds above 0"));
        }

        if (JsonChecks.TryGetRequired(rule, string.Empty, "route", problems, out var route))
        {
            ValidateRoute(route, problems);
        }

        if (rule.TryGetProperty("present", out var present))
        {
            ValidatePresent(present, problems);
        }

        return problems;
    }

    private static void ValidateRoute(JsonElement route, List<ProblemItem> problems)
    {
        if (!JsonChecks.RequireObject(route, "route", problems, "recipients", "channels"))
        {
            return;
        }

        if (JsonChecks.TryGetRequired(route, "route", "recipients", problems, out var recipients))
        {
            if (recipients.ValueKind != JsonValueKind.Array)
            {
                problems.Add(new ProblemItem("route.recipients", "must be an array"));
            }
            else
            {
                var index = 0;

                foreach (var recipient in recipients.EnumerateArray())
                {
                    ValidateRecipient(recipient, $"route.recipients[{index++}]", problems);
                }
            }
        }

        if (route.TryGetProperty("channels", out var channels) && channels.ValueKind != JsonValueKind.Null)
        {
            if (channels.ValueKind != JsonValueKind.Array)
            {
                problems.Add(new ProblemItem("route.channels", "must be an array"));

                return;
            }

            var index = 0;

            foreach (var channel in channels.EnumerateArray())
            {
                JsonChecks.CheckOneOf(channel, $"route.channels[{index++}]", problems, "ui", "email", "sms", "call");
            }
        }
    }

    private static void ValidateRecipient(JsonElement recipient, string path, List<ProblemItem> problems)
    {
        if (!JsonChecks.RequireObject(recipient, path, problems, "team", "user", "on_call"))
        {
            return;
        }

        var hasTeam = recipient.TryGetProperty("team", out var team);
        var hasUser = recipient.TryGetProperty("user", out var user);
        var onCall = recipient.TryGetProperty("on_call", out var onCallValue);

        if (hasTeam == hasUser || (onCall && hasUser))
        {
            problems.Add(new ProblemItem(path, "must be {team}, {team, on_call: true} or {user}"));

            return;
        }

        JsonChecks.CheckString(hasTeam ? team : user, JsonChecks.Join(path, hasTeam ? "team" : "user"), problems);

        if (onCall)
        {
            JsonChecks.CheckBool(onCallValue, JsonChecks.Join(path, "on_call"), problems);
        }
    }

    private static void ValidatePresent(JsonElement present, List<ProblemItem> problems)
    {
        if (!JsonChecks.RequireObject(present, "present", problems, "title", "body", "fields", "color"))
        {
            return;
        }

        foreach (var name in new[] { "title", "body" })
        {
            if (!present.TryGetProperty(name, out var template) || template.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            var path = JsonChecks.Join("present", name);

            if (template.ValueKind != JsonValueKind.String)
            {
                problems.Add(new ProblemItem(path, "must be a string"));

                continue;
            }

            foreach (var placeholder in JsonChecks.TemplatePaths(template.GetString()!).Where(placeholder => !JsonChecks.IsPath(placeholder)))
            {
                problems.Add(new ProblemItem(path, $"bad template path '{{{{{placeholder}}}}}'"));
            }
        }

        if (present.TryGetProperty("fields", out var fields))
        {
            JsonChecks.CheckPathList(fields, "present.fields", problems);
        }

        if (present.TryGetProperty("color", out var color) && color.ValueKind != JsonValueKind.Null)
        {
            JsonChecks.CheckOneOf(color, "present.color", problems, "red", "orange", "yellow", "blue", "grey");
        }
    }
}
