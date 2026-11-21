using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using AlertApi.Domain.Models;
using AlertApi.Domain.ValueObjects;

namespace AlertApi.Infrastructure.Data.Configurations;

public sealed class SourceConfiguration : IEntityTypeConfiguration<Source>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public void Configure(EntityTypeBuilder<Source> builder)
    {
        builder.ToTable("sources");

        builder.HasKey(source => source.Id)
            .HasName("PK_sources");

        builder.Property(source => source.Id)
            .HasColumnName("id");

        builder.Property(source => source.Kind)
            .HasColumnName("kind")
            .HasConversion(
                kind => kind.ToString().ToLowerInvariant(),
                value => Enum.Parse<SourceKind>(value, ignoreCase: true))
            .IsRequired();

        // Stored as {"topic": ..., "brokers": ...}, the format of contracts/schemas/ingest.md.
        builder.Property(source => source.Config)
            .HasColumnName("config")
            .HasColumnType("jsonb")
            .HasConversion(
                config => JsonSerializer.Serialize(config, JsonOptions),
                json => JsonSerializer.Deserialize<SourceConfig>(json, JsonOptions)!)
            .IsRequired();

        builder.Property(source => source.Enabled)
            .HasColumnName("enabled")
            .IsRequired();

        builder.Property(source => source.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.ToTable(table => table.HasCheckConstraint("CK_sources_kind", "kind IN ('http', 'kafka')"));
    }
}
