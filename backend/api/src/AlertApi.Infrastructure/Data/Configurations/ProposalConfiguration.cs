using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using AlertApi.Domain.Models;

namespace AlertApi.Infrastructure.Data.Configurations;

public sealed class ProposalConfiguration : IEntityTypeConfiguration<Proposal>
{
    public void Configure(EntityTypeBuilder<Proposal> builder)
    {
        builder.ToTable("proposals");
        builder.HasKey(proposal => proposal.Id).HasName("PK_proposals");
        builder.Property(proposal => proposal.Id).HasColumnName("id");
        builder.Property(proposal => proposal.AlertId).HasColumnName("alert_id").IsRequired();
        builder.Property(proposal => proposal.Kind).HasColumnName("kind").IsRequired();
        builder.Property(proposal => proposal.Body).HasColumnName("body").HasColumnType("jsonb").IsRequired();
        builder.Property(proposal => proposal.Author).HasColumnName("author").IsRequired();
        builder.Property(proposal => proposal.Status).HasColumnName("status").IsRequired();
        builder.Property(proposal => proposal.DecidedBy).HasColumnName("decided_by");
        builder.Property(proposal => proposal.CreatedAt).HasColumnName("created_at");
        builder.Property(proposal => proposal.DecidedAt).HasColumnName("decided_at");
        builder.Property(proposal => proposal.IdempotencyKey).HasColumnName("idempotency_key");
        builder.HasIndex(proposal => proposal.IdempotencyKey).IsUnique().HasDatabaseName("UQ_proposals_idempotency_key");
        builder.HasIndex(proposal => new { proposal.Status, proposal.CreatedAt }).HasDatabaseName("IX_proposals_status_created_at");
        builder.HasIndex(proposal => new { proposal.AlertId, proposal.Kind }).HasDatabaseName("IX_proposals_alert_id_kind");
    }
}
