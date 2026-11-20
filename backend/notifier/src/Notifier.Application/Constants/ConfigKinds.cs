namespace Notifier.Application.Constants;

// The config.changes kinds Notifier copies (contracts/events.md §6); the rest are other services' copies.
public static class ConfigKinds
{
    public const string Rule = "rule";
    public const string Team = "team";
    public const string User = "user";
    public const string Membership = "membership";
    public const string Preferences = "preferences";
    public const string Runbook = "runbook";
}
