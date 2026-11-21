using System.Linq.Expressions;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

using Notifier.Application.Serialization;
using Notifier.Domain.Models;
using Notifier.Domain.ValueObjects;

namespace Notifier.Infrastructure.Data;

public class NotifierDbContext : DbContext
{
    // Lease column used only by the outbox claim SQL; the domain never sees it.
    public const string LockedUntil = "locked_until";

    public NotifierDbContext(DbContextOptions<NotifierDbContext> options)
        : base(options)
    {
    }

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<Alert> Alerts => Set<Alert>();

    public DbSet<Rule> Rules => Set<Rule>();

    public DbSet<Team> Teams => Set<Team>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Membership> Memberships => Set<Membership>();

    public DbSet<UserPreferences> Preferences => Set<UserPreferences>();

    public DbSet<Runbook> Runbooks => Set<Runbook>();

    public DbSet<RateCounter> RateCounters => Set<RateCounter>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<ProcessedChange> ProcessedChanges => Set<ProcessedChange>();

    // A repository that needs a row lock (rate counters) opens a transaction; saving ends it, so the lock
    // is held exactly until the state it protects is written. Without one, this is a plain save.
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var saved = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (Database.CurrentTransaction is { } transaction)
        {
            await transaction.CommitAsync(cancellationToken);
            await transaction.DisposeAsync();
        }

        return saved;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureNotifications(modelBuilder);
        ConfigureAlerts(modelBuilder);
        ConfigureConfigCopies(modelBuilder);
        ConfigureRateCounters(modelBuilder);
        ConfigureOutboxMessages(modelBuilder);
        ConfigureProcessedChanges(modelBuilder);
    }

    private static void ConfigureNotifications(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Notification>(entity =>
        {
            entity.ToTable("notifications");
            entity.HasKey(notification => notification.Id).HasName("PK_notifications");
            entity.Ignore(notification => notification.IsOpen);

            entity.Property(notification => notification.Id).HasColumnName("id").HasMaxLength(64);
            entity.Property(notification => notification.AlertId).HasColumnName("alert_id").HasMaxLength(128);
            entity.Property(notification => notification.IncidentId).HasColumnName("incident_id").HasMaxLength(128);
            entity.Property(notification => notification.GroupKey).HasColumnName("group_key").HasMaxLength(128);
            entity.Property(notification => notification.UserId).HasColumnName("user_id").HasMaxLength(128);
            entity.Property(notification => notification.Channel).HasColumnName("channel").HasMaxLength(32);
            entity.Property(notification => notification.ChangeId).HasColumnName("change_id").HasMaxLength(128);
            entity.Property(notification => notification.EscalationStep).HasColumnName("escalation_step");
            entity.Property(notification => notification.Reason).HasColumnName("reason");
            entity.Property(notification => notification.Status).HasColumnName("status").HasMaxLength(16)
                .HasConversion(LowercaseEnum<NotificationStatus>());
            entity.Property(notification => notification.SendAt).HasColumnName("send_at");
            entity.Property(notification => notification.Attempts).HasColumnName("attempts");
            entity.Property(notification => notification.Recipient).HasColumnName("recipient").HasColumnType("jsonb")
                .HasConversion(Json<Recipient>());
            entity.Property(notification => notification.Content).HasColumnName("content").HasColumnType("jsonb")
                .HasConversion(Json<NotificationContent>());
            entity.Property(notification => notification.Rendered).HasColumnName("rendered");
            entity.Property(notification => notification.ProviderId).HasColumnName("provider_id").HasMaxLength(256);
            entity.Property(notification => notification.SentAt).HasColumnName("sent_at");
            entity.Property(notification => notification.Error).HasColumnName("error");
            entity.Property(notification => notification.CreatedAt).HasColumnName("created_at");
            entity.Property(notification => notification.UpdatedAt).HasColumnName("updated_at");

            // Only open rows are claimable; the claim's ORDER BY send_at LIMIT n walks this index and stops after n rows.
            entity.HasIndex(notification => notification.SendAt)
                .HasDatabaseName("IX_notifications_due")
                .HasFilter("status IN ('pending', 'failed')");
            entity.HasIndex(notification => notification.AlertId).HasDatabaseName("IX_notifications_alert_id");
            entity.HasIndex(notification => new { notification.UserId, notification.GroupKey })
                .HasDatabaseName("IX_notifications_user_id_group_key");
        });
    }

    private static void ConfigureAlerts(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Alert>(entity =>
        {
            entity.ToTable("alerts");
            entity.HasKey(alert => alert.Id).HasName("PK_alerts");
            entity.Ignore(alert => alert.IsOpen);

            entity.Property(alert => alert.Id).HasColumnName("id").HasMaxLength(128);
            entity.Property(alert => alert.RuleId).HasColumnName("rule_id").HasMaxLength(128);
            entity.Property(alert => alert.IncidentId).HasColumnName("incident_id").HasMaxLength(128);
            entity.Property(alert => alert.Status).HasColumnName("status").HasMaxLength(16)
                .HasConversion(LowercaseEnum<AlertStatus>());
            entity.Property(alert => alert.Severity).HasColumnName("severity").HasMaxLength(16);
            entity.Property(alert => alert.Body).HasColumnName("body").HasColumnType("jsonb");
            entity.Property(alert => alert.UpdatedAt).HasColumnName("updated_at");
        });
    }

    private static void ConfigureConfigCopies(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Rule>(entity =>
        {
            entity.ToTable("rules");
            entity.HasKey(rule => rule.Id).HasName("PK_rules");

            entity.Property(rule => rule.Id).HasColumnName("id").HasMaxLength(128);
            entity.Property(rule => rule.TeamId).HasColumnName("team_id").HasMaxLength(128);
            entity.Property(rule => rule.Enabled).HasColumnName("enabled");
            entity.Property(rule => rule.Body).HasColumnName("body").HasColumnType("jsonb");
            entity.Property(rule => rule.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<Team>(entity =>
        {
            entity.ToTable("teams");
            entity.HasKey(team => team.Id).HasName("PK_teams");

            entity.Property(team => team.Id).HasColumnName("id").HasMaxLength(128);
            entity.Property(team => team.Name).HasColumnName("name");
            entity.Property(team => team.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(user => user.Id).HasName("PK_users");

            entity.Property(user => user.Id).HasColumnName("id").HasMaxLength(128);
            entity.Property(user => user.Name).HasColumnName("name");
            entity.Property(user => user.Email).HasColumnName("email");
            entity.Property(user => user.Phone).HasColumnName("phone");
            entity.Property(user => user.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<Membership>(entity =>
        {
            entity.ToTable("memberships");
            entity.HasKey(membership => new { membership.TeamId, membership.UserId }).HasName("PK_memberships");

            entity.Property(membership => membership.TeamId).HasColumnName("team_id").HasMaxLength(128);
            entity.Property(membership => membership.UserId).HasColumnName("user_id").HasMaxLength(128);
            entity.Property(membership => membership.OnCall).HasColumnName("on_call");
            entity.Property(membership => membership.UpdatedAt).HasColumnName("updated_at");

            entity.HasIndex(membership => membership.UserId).HasDatabaseName("IX_memberships_user_id");
        });

        modelBuilder.Entity<UserPreferences>(entity =>
        {
            entity.ToTable("preferences");
            entity.HasKey(preferences => preferences.UserId).HasName("PK_preferences");

            entity.Property(preferences => preferences.UserId).HasColumnName("user_id").HasMaxLength(128);
            entity.Property(preferences => preferences.Body).HasColumnName("body").HasColumnType("jsonb");
            entity.Property(preferences => preferences.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<Runbook>(entity =>
        {
            entity.ToTable("runbooks");
            entity.HasKey(runbook => runbook.Id).HasName("PK_runbooks");
            entity.Ignore(runbook => runbook.IsActive);

            entity.Property(runbook => runbook.Id).HasColumnName("id").HasMaxLength(128);
            entity.Property(runbook => runbook.TeamId).HasColumnName("team_id").HasMaxLength(128);
            entity.Property(runbook => runbook.Title).HasColumnName("title");
            entity.Property(runbook => runbook.Canonical).HasColumnName("canonical").HasColumnType("jsonb");
            entity.Property(runbook => runbook.Status).HasColumnName("status").HasMaxLength(16);
            entity.Property(runbook => runbook.Version).HasColumnName("version");
            entity.Property(runbook => runbook.UpdatedAt).HasColumnName("updated_at");
        });
    }

    private static void ConfigureRateCounters(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RateCounter>(entity =>
        {
            entity.ToTable("rate_counters");
            entity.HasKey(counter => counter.UserId).HasName("PK_rate_counters");

            entity.Property(counter => counter.UserId).HasColumnName("user_id").HasMaxLength(128);
            entity.Property(counter => counter.WindowStart).HasColumnName("window_start");
            entity.Property(counter => counter.PushCount).HasColumnName("push_count");
            entity.Property(counter => counter.FoldedCount).HasColumnName("folded_count");
        });
    }

    private static void ConfigureOutboxMessages(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox");
            entity.HasKey(message => message.Id).HasName("PK_outbox");

            entity.Property(message => message.Id).HasColumnName("id");
            entity.Property(message => message.Sequence).HasColumnName("sequence").UseIdentityAlwaysColumn();
            entity.Property(message => message.Topic).HasColumnName("topic").HasMaxLength(128);
            entity.Property(message => message.Key).HasColumnName("key").HasMaxLength(128);
            entity.Property(message => message.Payload).HasColumnName("payload").HasColumnType("jsonb");
            entity.Property(message => message.CreatedAt).HasColumnName("created_at");
            entity.Property(message => message.ProcessedAt).HasColumnName("processed_at");
            entity.Property(message => message.Attempts).HasColumnName("attempts");
            entity.Property(message => message.Error).HasColumnName("error");
            entity.Property<DateTime?>(LockedUntil).HasColumnName(LockedUntil);

            entity.HasIndex(message => message.Sequence)
                .HasDatabaseName("IX_outbox_pending")
                .HasFilter("processed_at IS NULL");
        });
    }

    private static void ConfigureProcessedChanges(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProcessedChange>(entity =>
        {
            entity.ToTable("processed_changes");
            entity.HasKey(change => change.ChangeId).HasName("PK_processed_changes");

            entity.Property(change => change.ChangeId).HasColumnName("change_id").HasMaxLength(128);
            entity.Property(change => change.ProcessedAt).HasColumnName("processed_at");
        });
    }

    private static ValueConverter<TEnum, string> LowercaseEnum<TEnum>()
        where TEnum : struct, Enum
    {
        return new ValueConverter<TEnum, string>(
            value => value.ToString().ToLowerInvariant(),
            value => Enum.Parse<TEnum>(value, true));
    }

    // Value objects are immutable records, so a whole-object JSON column needs no change tracking of its own.
    private static ValueConverter<T, string> Json<T>()
        where T : class
    {
        Expression<Func<T, string>> serialize = value => JsonSerializer.Serialize(value, NotifierJson.Options);
        Expression<Func<string, T>> deserialize = value => JsonSerializer.Deserialize<T>(value, NotifierJson.Options)!;

        return new ValueConverter<T, string>(serialize, deserialize);
    }
}
