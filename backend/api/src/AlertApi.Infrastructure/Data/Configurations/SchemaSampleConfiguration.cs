using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using AlertApi.Domain.Models;

namespace AlertApi.Infrastructure.Data.Configurations;

public sealed class SchemaSampleConfiguration : IEntityTypeConfiguration<SchemaSample>
{
    public void Configure(EntityTypeBuilder<SchemaSample> builder)
    {
        builder.ToTable("schema_samples");
        builder.HasKey(sample => sample.Fingerprint).HasName("PK_schema_samples");
        builder.Property(sample => sample.Fingerprint).HasColumnName("fingerprint");
        builder.Property(sample => sample.SourceId).HasColumnName("source_id").IsRequired();
        builder.Property(sample => sample.Sample).HasColumnName("sample").HasColumnType("jsonb").IsRequired();
        builder.Property(sample => sample.Status).HasColumnName("status").IsRequired();
        builder.Property(sample => sample.Error).HasColumnName("error");
        builder.Property(sample => sample.CreatedAt).HasColumnName("created_at");
        builder.HasIndex(sample => sample.Status).HasDatabaseName("IX_schema_samples_status");
    }
}
