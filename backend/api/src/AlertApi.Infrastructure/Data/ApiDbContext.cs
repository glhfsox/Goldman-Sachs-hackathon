using Microsoft.EntityFrameworkCore;

using AlertApi.Application.Interfaces;
using AlertApi.Domain.Models;

namespace AlertApi.Infrastructure.Data;

public class ApiDbContext : DbContext, IUnitOfWork
{
    public ApiDbContext(DbContextOptions<ApiDbContext> options)
        : base(options)
    {
    }

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApiDbContext).Assembly);
    }
}
