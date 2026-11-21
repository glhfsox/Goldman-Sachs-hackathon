using Microsoft.EntityFrameworkCore;

using Ingest.Api.Endpoints;
using Ingest.Api.Infrastructure;
using Ingest.Application.Interfaces;
using Ingest.Application.Services;
using Ingest.Domain.Repositories;
using Ingest.Infrastructure.Data;
using Ingest.Infrastructure.Kafka;
using Ingest.Infrastructure.Registry;
using Ingest.Infrastructure.Repositories;

var builder = WebApplication.CreateBuilder(args);

var databaseUrl = builder.Configuration["DATABASE_URL"]
    ?? throw new InvalidOperationException(
        "DATABASE_URL is not set. Set it to postgres://user:password@host:port/database (see backend/ingest/.env.example).");

builder.Services.AddDbContext<IngestDbContext>(options =>
    options.UseNpgsql(DatabaseUrl.ToConnectionString(databaseUrl)));
builder.Services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<IngestDbContext>());
builder.Services.AddScoped<ISourceRepository, SourceRepository>();
builder.Services.AddScoped<ISourceService, SourceService>();
builder.Services.AddSingleton<ISourceRegistry, InMemorySourceRegistry>();

var kafkaBrokers = builder.Configuration["KAFKA_BROKERS"]
    ?? throw new InvalidOperationException(
        "KAFKA_BROKERS is not set. Set it to the internal cluster, for example kafka:9092 (see backend/ingest/.env.example).");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IRawEventPublisher>(_ => new KafkaRawEventPublisher(kafkaBrokers));
builder.Services.AddScoped<IIngestService, IngestService>();

builder.Services.AddHostedService(provider => new ConfigChangesConsumer(
    provider.GetRequiredService<IServiceScopeFactory>(),
    kafkaBrokers,
    provider.GetRequiredService<ILogger<ConfigChangesConsumer>>()));
builder.Services.AddHostedService(provider => new SourceRegistryRefresher(
    provider.GetRequiredService<ISourceRegistry>(),
    TimeSpan.FromSeconds(5),
    provider.GetRequiredService<ILogger<SourceRegistryRefresher>>()));

// Responses use snake_case like every record in contracts/events.md (event_id).
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower);

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var context = scope.ServiceProvider.GetRequiredService<IngestDbContext>();
    await context.Database.MigrateAsync();
}

await app.Services.GetRequiredService<ISourceRegistry>().RefreshAsync();

app.UseExceptionHandler();

app.MapHealthEndpoints();
app.MapIngestEndpoints();

app.Run();

// Lets the test project reach Program through WebApplicationFactory.
public partial class Program;
