using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using AlertApi.Domain.Models;

namespace AlertApi.Infrastructure.Data.Configurations;

public sealed class RunbookRunConfiguration : IEntityTypeConfiguration<RunbookRun>
{
    public void Configure(EntityTypeBuilder<RunbookRun> builder)
    {
        builder.ToTable("runbook_runs");
        builder.HasKey(run => run.Id).HasName("PK_runbook_runs");
        builder.Property(run => run.Id).HasColumnName("id");
        builder.Property(run => run.AlertId).HasColumnName("alert_id").IsRequired();
        builder.Property(run => run.RuleId).HasColumnName("rule_id").IsRequired();
        builder.Property(run => run.TeamId).HasColumnName("team_id").IsRequired();
        builder.Property(run => run.RunbookId).HasColumnName("runbook_id").IsRequired();
        builder.Property(run => run.RunbookVersion).HasColumnName("runbook_version");
        builder.Property(run => run.Risk).HasColumnName("risk").IsRequired();
        builder.Property(run => run.Trigger).HasColumnName("trigger").IsRequired();
        builder.Property(run => run.Status).HasColumnName("status").IsRequired();
        builder.Property(run => run.StartAt).HasColumnName("start_at");
        builder.Property(run => run.ApprovedBy).HasColumnName("approved_by");
        builder.Property(run => run.ApprovedAt).HasColumnName("approved_at");
        builder.Property(run => run.CreatedAt).HasColumnName("created_at");
        builder.Property(run => run.StartedAt).HasColumnName("started_at");
        builder.Property(run => run.FinishedAt).HasColumnName("finished_at");
        builder.Property(run => run.CancelledBy).HasColumnName("cancelled_by");
        builder.Ignore(run => run.IsLive);
        builder.HasIndex(run => new { run.Status, run.FinishedAt }).HasDatabaseName("IX_runbook_runs_status_finished_at");

        // One live run per alert, enforced by the database so two replicas cannot both start one.
        builder.HasIndex(run => run.AlertId)
            .IsUnique()
            .HasFilter("status in ('scheduled', 'running')")
            .HasDatabaseName(RunConstraints.LiveRunPerAlert);
        builder.HasIndex(run => new { run.AlertId, run.CreatedAt }).HasDatabaseName("IX_runbook_runs_alert_id_created_at");
    }
}
