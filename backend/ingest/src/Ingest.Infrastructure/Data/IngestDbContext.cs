using Microsoft.EntityFrameworkCore;

using Ingest.Application.Interfaces;
using Ingest.Domain.Models;

namespace Ingest.Infrastructure.Data;

public class IngestDbContext : DbContext, IUnitOfWork
{
    public IngestDbContext(DbContextOptions<IngestDbContext> options)
        : base(options)
    {
    }

    public DbSet<Source> Sources => Set<Source>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IngestDbContext).Assembly);
    }
}
