using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using AlertApi.Domain.Models;

namespace AlertApi.Infrastructure.Data.Configurations;

public sealed class AlertEventConfiguration : IEntityTypeConfiguration<AlertEvent>
{
    public void Configure(EntityTypeBuilder<AlertEvent> builder)
    {
        builder.ToTable("alert_events");
        builder.HasKey(alertEvent => alertEvent.Id).HasName("PK_alert_events");
        builder.Property(alertEvent => alertEvent.Id).HasColumnName("id");
        builder.Property(alertEvent => alertEvent.AlertId).HasColumnName("alert_id").IsRequired();
        builder.Property(alertEvent => alertEvent.At).HasColumnName("at");
        builder.Property(alertEvent => alertEvent.Actor).HasColumnName("actor").IsRequired();
        builder.Property(alertEvent => alertEvent.Kind).HasColumnName("kind").IsRequired();
        builder.Property(alertEvent => alertEvent.Data).HasColumnName("data").HasColumnType("jsonb").IsRequired();
        builder.HasIndex(alertEvent => new { alertEvent.AlertId, alertEvent.At }).HasDatabaseName("IX_alert_events_alert_id_at");
    }
}
