using Microsoft.EntityFrameworkCore;

using AlertApi.Application.Exceptions;
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

    public DbSet<Runbook> Runbooks => Set<Runbook>();

    public DbSet<ActionTarget> ActionTargets => Set<ActionTarget>();

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    // Services save through IUnitOfWork; a lost race on a concurrency token (runbooks.version) becomes a 409.
    async Task<int> IUnitOfWork.SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new ConcurrentUpdateException(exception);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApiDbContext).Assembly);
    }
}
