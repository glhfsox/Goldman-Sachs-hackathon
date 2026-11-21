using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using AlertApi.Domain.Models;

namespace AlertApi.Infrastructure.Data.Configurations;

public sealed class RunbookConfiguration : IEntityTypeConfiguration<Runbook>
{
    public void Configure(EntityTypeBuilder<Runbook> builder)
    {
        builder.ToTable("runbooks");
        builder.HasKey(runbook => runbook.Id).HasName("PK_runbooks");
        builder.Property(runbook => runbook.Id).HasColumnName("id");
        builder.Property(runbook => runbook.TeamId).HasColumnName("team_id").IsRequired();
        builder.Property(runbook => runbook.Title).HasColumnName("title").IsRequired();
        builder.Property(runbook => runbook.Format).HasColumnName("format").IsRequired();
        builder.Property(runbook => runbook.Source).HasColumnName("source").IsRequired();
        builder.Property(runbook => runbook.Canonical).HasColumnName("canonical").HasColumnType("jsonb");
        builder.Property(runbook => runbook.Status).HasColumnName("status").IsRequired();
        builder.Property(runbook => runbook.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(runbook => runbook.AcceptedBy).HasColumnName("accepted_by");
        builder.Property(runbook => runbook.AcceptedAt).HasColumnName("accepted_at");
        builder.Property(runbook => runbook.UpdatedAt).HasColumnName("updated_at");
        builder.HasIndex(runbook => runbook.TeamId).HasDatabaseName("IX_runbooks_team_id");
    }
}
