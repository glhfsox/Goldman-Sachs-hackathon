using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using AlertApi.Domain.Models;

namespace AlertApi.Infrastructure.Data.Configurations;

public sealed class ActionRunConfiguration : IEntityTypeConfiguration<ActionRun>
{
    public void Configure(EntityTypeBuilder<ActionRun> builder)
    {
        builder.ToTable("action_runs");
        builder.HasKey(node => node.Id).HasName("PK_action_runs");
        builder.Property(node => node.Id).HasColumnName("id");
        builder.Property(node => node.RunId).HasColumnName("run_id").IsRequired();
        builder.Property(node => node.AlertId).HasColumnName("alert_id").IsRequired();
        builder.Property(node => node.RunbookId).HasColumnName("runbook_id").IsRequired();
        builder.Property(node => node.NodeId).HasColumnName("node_id").IsRequired();
        builder.Property(node => node.StepIndex).HasColumnName("step_index");
        builder.Property(node => node.Text).HasColumnName("text").IsRequired();
        builder.Property(node => node.Action).HasColumnName("action");
        builder.Property(node => node.Params).HasColumnName("params").HasColumnType("jsonb");
        builder.Property(node => node.Risk).HasColumnName("risk").IsRequired();
        builder.Property(node => node.TargetId).HasColumnName("target_id");
        builder.Property(node => node.TargetVersion).HasColumnName("target_version");
        builder.Property(node => node.Request).HasColumnName("request").HasColumnType("jsonb");
        builder.Property(node => node.Needs).HasColumnName("needs").HasColumnType("jsonb").IsRequired();
        builder.Property(node => node.IfStep).HasColumnName("if_step");
        builder.Property(node => node.IfStatus).HasColumnName("if_status");
        builder.Property(node => node.Retries).HasColumnName("retries");
        builder.Property(node => node.TimeoutSeconds).HasColumnName("timeout_s");
        builder.Property(node => node.Status).HasColumnName("status").IsRequired();
        builder.Property(node => node.Attempt).HasColumnName("attempt");
        builder.Property(node => node.NotBefore).HasColumnName("not_before");
        builder.Property(node => node.LeaseUntil).HasColumnName("lease_until");
        builder.Property(node => node.IdempotencyKey).HasColumnName("idempotency_key").IsRequired();
        builder.Property(node => node.Result).HasColumnName("result").HasColumnType("jsonb");
        builder.Property(node => node.StartedAt).HasColumnName("started_at");
        builder.Property(node => node.FinishedAt).HasColumnName("finished_at");
        builder.Ignore(node => node.IsManual);
        builder.HasIndex(node => new { node.RunId, node.NodeId }).IsUnique().HasDatabaseName("UQ_action_runs_run_id_node_id");
        builder.HasIndex(node => node.IdempotencyKey).IsUnique().HasDatabaseName("UQ_action_runs_idempotency_key");
        builder.HasIndex(node => node.AlertId).HasDatabaseName("IX_action_runs_alert_id");

        // The executor (step 3c) claims ready nodes and nodes whose lease ran out.
        builder.HasIndex(node => node.NotBefore)
            .HasFilter("status in ('ready', 'running')")
            .HasDatabaseName("IX_action_runs_claimable");
    }
}
