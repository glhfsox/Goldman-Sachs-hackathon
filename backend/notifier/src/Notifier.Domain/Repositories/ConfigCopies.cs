using Notifier.Domain.Models;

namespace Notifier.Domain.Repositories;

// Every read-only configuration copy at once: small tables, read together when planning.
public sealed record ConfigCopies(
    IReadOnlyList<Rule> Rules,
    IReadOnlyList<User> Users,
    IReadOnlyList<Membership> Memberships,
    IReadOnlyList<UserPreferences> Preferences,
    IReadOnlyList<Runbook> Runbooks);
