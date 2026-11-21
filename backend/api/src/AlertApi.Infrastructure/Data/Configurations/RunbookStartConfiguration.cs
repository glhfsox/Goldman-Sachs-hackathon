using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using AlertApi.Domain.Models;

namespace AlertApi.Infrastructure.Data.Configurations;

public sealed class RunbookStartConfiguration : IEntityTypeConfiguration<RunbookStart>
{
    public void Configure(EntityTypeBuilder<RunbookStart> builder)
    {
        builder.ToTable("runbook_starts");
        builder.HasKey(start => new { start.RuleId, start.RunbookId }).HasName("PK_runbook_starts");
        builder.Property(start => start.RuleId).HasColumnName("rule_id");
        builder.Property(start => start.RunbookId).HasColumnName("runbook_id");
        builder.Property(start => start.Start).HasColumnName("start").IsRequired();
        builder.Property(start => start.SetBy).HasColumnName("set_by").IsRequired();
        builder.Property(start => start.UpdatedAt).HasColumnName("updated_at");
        builder.ToTable(table => table.HasCheckConstraint("CK_runbook_starts_start", "start in ('auto', 'approval')"));
    }
}
