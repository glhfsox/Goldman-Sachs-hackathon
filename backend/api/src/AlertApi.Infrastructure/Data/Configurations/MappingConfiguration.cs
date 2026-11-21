using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using AlertApi.Domain.Models;

namespace AlertApi.Infrastructure.Data.Configurations;

public sealed class MappingConfiguration : IEntityTypeConfiguration<Mapping>
{
    public void Configure(EntityTypeBuilder<Mapping> builder)
    {
        builder.ToTable("mappings", table =>
        {
            table.HasCheckConstraint("CK_mappings_status", "status IN ('active', 'disabled')");
            table.HasCheckConstraint("CK_mappings_origin", "origin IN ('seed', 'human', 'llm')");
        });
        builder.HasKey(mapping => mapping.Id).HasName("PK_mappings");
        builder.Property(mapping => mapping.Id).HasColumnName("id");
        builder.Property(mapping => mapping.Status).HasColumnName("status").IsRequired();
        builder.Property(mapping => mapping.Origin).HasColumnName("origin").IsRequired();
        builder.Property(mapping => mapping.Priority).HasColumnName("priority");
        builder.Property(mapping => mapping.Body).HasColumnName("body").HasColumnType("jsonb").IsRequired();
        builder.Property(mapping => mapping.UpdatedAt).HasColumnName("updated_at");
    }
}
