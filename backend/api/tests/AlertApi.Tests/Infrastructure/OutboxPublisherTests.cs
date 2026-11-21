using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using AlertApi.Application.Exceptions;
using AlertApi.Application.Interfaces;
using AlertApi.Domain.Models;
using AlertApi.Infrastructure.Data;
using AlertApi.Infrastructure.Outbox;

namespace AlertApi.Tests.Infrastructure;

[Trait("Category", "Integration")]
public sealed class OutboxPublisherTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public OutboxPublisherTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task PublishBatch_SendsRowsInWriteOrderAndDeletesThem()
    {
        var publisher = new RecordingPublisher();
        await using var provider = await BuildProviderAsync(publisher);
        await AddMessagesAsync(provider, "a", "b", "c");

        var published = await CreateOutboxPublisher(provider).PublishBatchAsync(CancellationToken.None);

        Assert.Equal(3, published);
        Assert.Equal(["a", "b", "c"], publisher.Keys);
        Assert.Equal(0, await CountAsync(provider));
    }

    [Fact]
    public async Task PublishBatch_WhenKafkaFails_KeepsTheRows()
    {
        var publisher = new RecordingPublisher { FailAfter = 1 };
        await using var provider = await BuildProviderAsync(publisher);
        await AddMessagesAsync(provider, "a", "b");

        await Assert.ThrowsAsync<EventPublishFailedException>(
            () => CreateOutboxPublisher(provider).PublishBatchAsync(CancellationToken.None));

        // Rolled back: both rows stay, so "a" is sent again later (consumers are idempotent).
        Assert.Equal(2, await CountAsync(provider));
    }

    private async Task<ServiceProvider> BuildProviderAsync(IEventPublisher publisher)
    {
        var database = $"outbox_{Guid.NewGuid():N}";
        var admin = new Npgsql.NpgsqlConnection(_postgres.ConnectionString);
        await admin.OpenAsync();
        await using (admin)
        {
            await using var command = admin.CreateCommand();
            command.CommandText = $"CREATE DATABASE {database}";
            await command.ExecuteNonQueryAsync();
        }

        var builder = new Npgsql.NpgsqlConnectionStringBuilder(_postgres.ConnectionString) { Database = database };
        var services = new ServiceCollection();
        services.AddDbContext<ApiDbContext>(options => options.UseNpgsql(builder.ConnectionString));
        services.AddSingleton(publisher);
        var provider = services.BuildServiceProvider();

        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
        await context.Database.MigrateAsync();
        // The migration queues the seeded sources; these tests count only their own rows.
        await context.Database.ExecuteSqlRawAsync("DELETE FROM outbox");

        return provider;
    }

    private static OutboxPublisher CreateOutboxPublisher(ServiceProvider provider)
    {
        return new OutboxPublisher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10),
            NullLogger<OutboxPublisher>.Instance);
    }

    private static async Task AddMessagesAsync(ServiceProvider provider, params string[] keys)
    {
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ApiDbContext>();

        foreach (var key in keys)
        {
            context.Outbox.Add(OutboxMessage.Create("config.changes", key, "{}", DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }
    }

    private static async Task<int> CountAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<ApiDbContext>().Outbox.CountAsync();
    }

    private sealed class RecordingPublisher : IEventPublisher
    {
        public List<string> Keys { get; } = [];

        public int? FailAfter { get; init; }

        public Task PublishAsync(string topic, string key, string payload, CancellationToken cancellationToken = default)
        {
            if (FailAfter is { } limit && Keys.Count >= limit)
            {
                throw new EventPublishFailedException(topic, new InvalidOperationException("broker down"));
            }

            Keys.Add(key);

            return Task.CompletedTask;
        }
    }
}
