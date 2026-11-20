using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Ingest.Application.Interfaces;
using Ingest.Domain.Models;
using Ingest.Domain.Repositories;
using Ingest.Domain.ValueObjects;
using Ingest.Infrastructure.Data;
using Ingest.Infrastructure.Registry;
using Ingest.Infrastructure.Repositories;

namespace Ingest.Tests.Infrastructure;

[Trait("Category", "Integration")]
public sealed class SourcePersistenceTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public SourcePersistenceTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task Migrate_OnEmptyDatabase_SeedsTheFourContractSources()
    {
        await using var provider = await BuildProviderAsync(Guid.NewGuid().ToString("N"));
        await using var scope = provider.CreateAsyncScope();

        var sources = await scope.ServiceProvider.GetRequiredService<ISourceRepository>().GetAllAsync();

        Assert.Equal(
            ["alertmanager-http", "pricing-logs-http", "trades-kafka", "workflows-kafka"],
            sources.Select(source => source.Id));
        var workflows = sources.Single(source => source.Id == "workflows-kafka");
        Assert.Equal(SourceKind.Kafka, workflows.Kind);
        Assert.Equal("source.workflows", workflows.Config.Topic);
        Assert.Null(workflows.Config.Brokers);
        Assert.True(workflows.Enabled);
        Assert.True(sources.All(source => source.UpdatedAt < new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task AddAsync_KafkaSourceWithBrokers_RoundTripsConfig()
    {
        await using var provider = await BuildProviderAsync(Guid.NewGuid().ToString("N"));
        await using (var scope = provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<ISourceRepository>();
            await repository.AddAsync(Source.Create(
                "other-kafka",
                SourceKind.Kafka,
                new SourceConfig("source.other", "other-cluster:9092"),
                enabled: false,
                DateTimeOffset.UtcNow));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        await using var readScope = provider.CreateAsyncScope();
        var stored = await readScope.ServiceProvider.GetRequiredService<ISourceRepository>().GetByIdAsync("other-kafka");

        Assert.Equal(new SourceConfig("source.other", "other-cluster:9092"), stored!.Config);
        Assert.False(stored.Enabled);
    }

    [Fact]
    public async Task Registry_AfterRefresh_FindsSourcesAndListsOnlyEnabled()
    {
        await using var provider = await BuildProviderAsync(Guid.NewGuid().ToString("N"));
        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISourceRepository>().AddAsync(Source.Create(
                "off-http", SourceKind.Http, SourceConfig.Empty, enabled: false, DateTimeOffset.UtcNow));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }
        var registry = provider.GetRequiredService<ISourceRegistry>();

        Assert.Null(registry.Find("workflows-kafka"));
        await registry.RefreshAsync();

        Assert.NotNull(registry.Find("workflows-kafka"));
        Assert.NotNull(registry.Find("off-http"));
        Assert.DoesNotContain(registry.GetEnabled(), source => source.Id == "off-http");
        Assert.Equal(4, registry.GetEnabled().Count);
    }

    // Each test gets its own database inside the shared container.
    private async Task<ServiceProvider> BuildProviderAsync(string databaseName)
    {
        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_postgres.ConnectionString)
        {
            Database = databaseName
        }.ConnectionString;

        var services = new ServiceCollection();
        services.AddDbContext<IngestDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<IngestDbContext>());
        services.AddScoped<ISourceRepository, SourceRepository>();
        services.AddSingleton<ISourceRegistry, InMemorySourceRegistry>();
        var provider = services.BuildServiceProvider();

        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IngestDbContext>().Database.MigrateAsync();

        return provider;
    }
}
