using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using AlertApi.Domain.Models;

namespace AlertApi.Infrastructure.Data.Configurations;

public sealed class AlertConfiguration : IEntityTypeConfiguration<Alert>
{
    public void Configure(EntityTypeBuilder<Alert> builder)
    {
        builder.ToTable("alerts");
        builder.HasKey(alert => alert.Id).HasName("PK_alerts");
        builder.Property(alert => alert.Id).HasColumnName("id");
        builder.Property(alert => alert.RuleId).HasColumnName("rule_id").IsRequired();
        builder.Property(alert => alert.TeamId).HasColumnName("team_id").IsRequired();
        builder.Property(alert => alert.IncidentId).HasColumnName("incident_id");
        builder.Property(alert => alert.Status).HasColumnName("status").IsRequired();
        builder.Property(alert => alert.Severity).HasColumnName("severity").IsRequired();
        builder.Property(alert => alert.Title).HasColumnName("title").IsRequired();
        builder.Property(alert => alert.Body).HasColumnName("body").IsRequired();
        builder.Property(alert => alert.Fields).HasColumnName("fields").HasColumnType("jsonb").IsRequired();
        builder.Property(alert => alert.Color).HasColumnName("color");
        builder.Property(alert => alert.Count).HasColumnName("count");
        builder.Property(alert => alert.FirstSeen).HasColumnName("first_seen");
        builder.Property(alert => alert.LastSeen).HasColumnName("last_seen");
        builder.Property(alert => alert.FirstReceivedAt).HasColumnName("first_received_at");
        builder.Property(alert => alert.RunbookId).HasColumnName("runbook_id");
        builder.Property(alert => alert.AckedBy).HasColumnName("acked_by");
        builder.Property(alert => alert.ResolvedBy).HasColumnName("resolved_by");
        builder.Property(alert => alert.SampleEvent).HasColumnName("sample_event").HasColumnType("jsonb").IsRequired();
        builder.HasIndex(alert => new { alert.TeamId, alert.FirstSeen }).HasDatabaseName("IX_alerts_team_id_first_seen");
        builder.HasIndex(alert => alert.FirstSeen).HasDatabaseName("IX_alerts_first_seen");
    }
}
