using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using AlertApi.Domain.Models;

namespace AlertApi.Infrastructure.Data.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(user => user.Id).HasName("PK_users");
        builder.Property(user => user.Id).HasColumnName("id");
        builder.Property(user => user.Name).HasColumnName("name").IsRequired();
        builder.Property(user => user.Email).HasColumnName("email").IsRequired();
        builder.Property(user => user.Phone).HasColumnName("phone").IsRequired();
    }
}
