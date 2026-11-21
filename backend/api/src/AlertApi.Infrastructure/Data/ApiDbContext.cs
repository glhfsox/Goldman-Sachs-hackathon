using Microsoft.EntityFrameworkCore;

using Npgsql;

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

    public DbSet<RunbookRun> Runs => Set<RunbookRun>();

    public DbSet<ActionRun> ActionRuns => Set<ActionRun>();

    public DbSet<Proposal> Proposals => Set<Proposal>();

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
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { ConstraintName: RunConstraints.LiveRunPerAlert })
        {
            throw new LiveRunExistsException(exception);
        }
    }

    async Task<IUnitOfWorkTransaction> IUnitOfWork.BeginTransactionAsync(CancellationToken cancellationToken)
    {
        return new Transaction(await Database.BeginTransactionAsync(cancellationToken));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApiDbContext).Assembly);
    }

    private sealed class Transaction : IUnitOfWorkTransaction
    {
        private readonly Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction _inner;

        public Transaction(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction inner)
        {
            _inner = inner;
        }

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await _inner.CommitAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
        }
    }
}
