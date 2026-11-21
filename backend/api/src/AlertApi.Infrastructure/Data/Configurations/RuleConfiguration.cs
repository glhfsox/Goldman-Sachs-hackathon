using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using AlertApi.Domain.Models;

namespace AlertApi.Infrastructure.Data.Configurations;

public sealed class RuleConfiguration : IEntityTypeConfiguration<Rule>
{
    public void Configure(EntityTypeBuilder<Rule> builder)
    {
        builder.ToTable("rules");
        builder.HasKey(rule => rule.Id).HasName("PK_rules");
        builder.Property(rule => rule.Id).HasColumnName("id");
        builder.Property(rule => rule.TeamId).HasColumnName("team_id").IsRequired();
        builder.Property(rule => rule.Enabled).HasColumnName("enabled");
        builder.Property(rule => rule.Body).HasColumnName("body").HasColumnType("jsonb").IsRequired();
        builder.Property(rule => rule.UpdatedAt).HasColumnName("updated_at");
        builder.HasIndex(rule => rule.TeamId).HasDatabaseName("IX_rules_team_id");
    }
}
