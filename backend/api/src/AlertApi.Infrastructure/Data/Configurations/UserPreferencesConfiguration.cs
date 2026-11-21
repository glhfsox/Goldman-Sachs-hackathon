using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using AlertApi.Domain.Models;

namespace AlertApi.Infrastructure.Data.Configurations;

public sealed class UserPreferencesConfiguration : IEntityTypeConfiguration<UserPreferences>
{
    public void Configure(EntityTypeBuilder<UserPreferences> builder)
    {
        builder.ToTable("preferences");
        builder.HasKey(preferences => preferences.UserId).HasName("PK_preferences");
        builder.Property(preferences => preferences.UserId).HasColumnName("user_id");
        builder.Property(preferences => preferences.Body).HasColumnName("body").HasColumnType("jsonb").IsRequired();
        builder.HasOne<User>().WithMany().HasForeignKey(preferences => preferences.UserId).HasConstraintName("FK_preferences_user");
    }
}
