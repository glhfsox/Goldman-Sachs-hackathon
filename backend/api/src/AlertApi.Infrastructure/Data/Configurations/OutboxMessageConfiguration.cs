using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using AlertApi.Domain.Models;

namespace AlertApi.Infrastructure.Data.Configurations;

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox");
        builder.HasKey(message => message.Id).HasName("PK_outbox");
        builder.Property(message => message.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(message => message.Topic).HasColumnName("topic").IsRequired();
        builder.Property(message => message.Key).HasColumnName("key").IsRequired();
        builder.Property(message => message.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        builder.Property(message => message.CreatedAt).HasColumnName("created_at");
    }
}
