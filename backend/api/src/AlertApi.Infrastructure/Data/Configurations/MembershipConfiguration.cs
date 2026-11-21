using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using AlertApi.Domain.Models;

namespace AlertApi.Infrastructure.Data.Configurations;

public sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> builder)
    {
        builder.ToTable("memberships");
        builder.HasKey(membership => new { membership.TeamId, membership.UserId }).HasName("PK_memberships");
        builder.Property(membership => membership.TeamId).HasColumnName("team_id");
        builder.Property(membership => membership.UserId).HasColumnName("user_id");
        builder.Property(membership => membership.OnCall).HasColumnName("on_call");
        builder.HasOne<Team>().WithMany().HasForeignKey(membership => membership.TeamId).HasConstraintName("FK_memberships_team");
        builder.HasOne<User>().WithMany().HasForeignKey(membership => membership.UserId).HasConstraintName("FK_memberships_user");
    }
}
