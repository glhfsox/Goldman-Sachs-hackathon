using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using AlertApi.Domain.Models;

namespace AlertApi.Infrastructure.Data.Configurations;

public sealed class ActionTargetConfiguration : IEntityTypeConfiguration<ActionTarget>
{
    public void Configure(EntityTypeBuilder<ActionTarget> builder)
    {
        builder.ToTable("action_targets");
        builder.HasKey(target => target.Id).HasName("PK_action_targets");
        builder.Property(target => target.Id).HasColumnName("id");
        builder.Property(target => target.BaseUrl).HasColumnName("base_url").IsRequired();
        builder.Property(target => target.Origins).HasColumnName("origins").HasColumnType("jsonb").IsRequired();
        builder.Property(target => target.Operations).HasColumnName("operations").HasColumnType("jsonb").IsRequired();
        builder.Property(target => target.SecretRef).HasColumnName("secret_ref");
        builder.Property(target => target.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(target => target.UpdatedAt).HasColumnName("updated_at");
    }
}
