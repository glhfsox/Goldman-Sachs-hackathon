using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using AlertApi.Domain.Models;

namespace AlertApi.Infrastructure.Data.Configurations;

public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");
        builder.HasKey(notification => notification.Id).HasName("PK_notifications");
        builder.Property(notification => notification.Id).HasColumnName("id");
        builder.Property(notification => notification.AlertId).HasColumnName("alert_id").IsRequired();
        builder.Property(notification => notification.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(notification => notification.Channel).HasColumnName("channel").IsRequired();
        builder.Property(notification => notification.Status).HasColumnName("status").IsRequired();
        builder.Property(notification => notification.Reason).HasColumnName("reason").IsRequired();
        builder.Property(notification => notification.SendAt).HasColumnName("send_at");
        builder.Property(notification => notification.SentAt).HasColumnName("sent_at");
        builder.Property(notification => notification.Error).HasColumnName("error");
        builder.HasIndex(notification => notification.AlertId).HasDatabaseName("IX_notifications_alert_id");
    }
}
