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

    public DbSet<Source> Sources => Set<Source>();

    public DbSet<Rule> Rules => Set<Rule>();

    public DbSet<Mapping> Mappings => Set<Mapping>();

    public DbSet<Team> Teams => Set<Team>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Membership> Memberships => Set<Membership>();

    public DbSet<ServiceDependency> ServiceDependencies => Set<ServiceDependency>();

    public DbSet<UserPreferences> Preferences => Set<UserPreferences>();

    public DbSet<Alert> Alerts => Set<Alert>();

    public DbSet<AlertEvent> AlertEvents => Set<AlertEvent>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<SchemaSample> SchemaSamples => Set<SchemaSample>();

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApiDbContext).Assembly);
    }
}
