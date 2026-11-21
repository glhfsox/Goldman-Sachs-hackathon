using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Ingest.Application.Interfaces;
using Ingest.Application.Services;
using Ingest.Domain.Models;
using Ingest.Domain.Repositories;
using Ingest.Infrastructure.Data;
using Ingest.Infrastructure.Kafka;
using Ingest.Infrastructure.Registry;
using Ingest.Infrastructure.Repositories;

namespace Ingest.Tests.Infrastructure;

// Real Kafka + Postgres: a config.changes record ends up in the sources table and the registry.
[Trait("Category", "Integration")]
public sealed class ConfigChangesConsumerTests : IClassFixture<PostgresFixture>, IClassFixture<KafkaFixture>
{
    private readonly PostgresFixture _postgres;
    private readonly KafkaFixture _kafka;

    public ConfigChangesConsumerTests(PostgresFixture postgres, KafkaFixture kafka)
    {
        _postgres = postgres;
        _kafka = kafka;
    }

    [Fact]
    public async Task Consumer_Records_UpsertNewerAndIgnoreOlderAndBad()
    {
        await EnsureTopicAsync();
        await using var provider = await BuildProviderAsync(Guid.NewGuid().ToString("N"));
        var registry = provider.GetRequiredService<ISourceRegistry>();
        using var consumer = new ConfigChangesConsumer(
            provider.GetRequiredService<IServiceScopeFactory>(),
            _kafka.Brokers,
            NullLogger<ConfigChangesConsumer>.Instance);
        using var refresher = new SourceRegistryRefresher(
            registry, TimeSpan.FromMilliseconds(200), NullLogger<SourceRegistryRefresher>.Instance);
        await consumer.StartAsync(CancellationToken.None);
        await refresher.StartAsync(CancellationToken.None);

        // A seeded row (updated_at 2000-01-01) is overridden by any real record.
        await ProduceAsync("source:workflows-kafka", Record("workflows-kafka", "kafka", """{"topic":"source.workflows.v2"}""", false, "2026-11-21T10:00:00Z"));
        // An older record, a bad record and another kind must change nothing and must not block the next record.
        await ProduceAsync("source:workflows-kafka", Record("workflows-kafka", "kafka", """{"topic":"old"}""", true, "2026-11-21T09:00:00Z"));
        await ProduceAsync("source:bad", "{ not json");
        await ProduceAsync("rule:r", """{"kind":"rule","id":"r","updated_at":"2026-11-21T10:00:00Z","body":{"id":"r"}}""");
        await ProduceAsync("source:new-http", Record("new-http", "http", "{}", true, "2026-11-21T10:00:01Z"));

        await WaitUntilAsync(() => registry.Find("new-http") is not null);
        await consumer.StopAsync(CancellationToken.None);
        await refresher.StopAsync(CancellationToken.None);

        var workflows = registry.Find("workflows-kafka")!;
        Assert.Equal("source.workflows.v2", workflows.Config.Topic);
        Assert.False(workflows.Enabled);
        Assert.Equal(4 + 1, (await ReadAllAsync(provider)).Count);
    }

    private static string Record(string id, string kind, string config, bool enabled, string updatedAt)
    {
        return $$$"""
            {"kind":"source","id":"{{{id}}}","updated_at":"{{{updatedAt}}}","deleted":false,
             "body":{"id":"{{{id}}}","kind":"{{{kind}}}","config":{{{config}}},"enabled":{{{enabled.ToString().ToLowerInvariant()}}}}}
            """;
    }

    private async Task ProduceAsync(string key, string value)
    {
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = _kafka.Brokers }).Build();

        await producer.ProduceAsync(ConfigChangesConsumer.Topic, new Message<string, string> { Key = key, Value = value });
    }

    private async Task EnsureTopicAsync()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _kafka.Brokers }).Build();

        try
        {
            await admin.CreateTopicsAsync([new TopicSpecification { Name = ConfigChangesConsumer.Topic, NumPartitions = 1 }]);
        }
        catch (CreateTopicsException exception) when (exception.Results.All(result => result.Error.Code == ErrorCode.TopicAlreadyExists))
        {
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);

        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
        }

        Assert.True(condition(), "The change did not reach the registry within 60 s.");
    }

    private static async Task<IReadOnlyList<Source>> ReadAllAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<ISourceRepository>().GetAllAsync();
    }

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
        services.AddScoped<ISourceService, SourceService>();
        services.AddSingleton<ISourceRegistry, InMemorySourceRegistry>();
        var provider = services.BuildServiceProvider();

        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IngestDbContext>().Database.MigrateAsync();

        return provider;
    }
}
