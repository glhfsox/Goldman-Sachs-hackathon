using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using AlertApi.Domain.Models;

namespace AlertApi.Infrastructure.Data.Configurations;

public sealed class ServiceDependencyConfiguration : IEntityTypeConfiguration<ServiceDependency>
{
    public void Configure(EntityTypeBuilder<ServiceDependency> builder)
    {
        builder.ToTable("service_deps");
        builder.HasKey(dependency => new { dependency.Service, dependency.DependsOn }).HasName("PK_service_deps");
        builder.Property(dependency => dependency.Service).HasColumnName("service");
        builder.Property(dependency => dependency.DependsOn).HasColumnName("depends_on");
    }
}
