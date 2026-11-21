using Microsoft.EntityFrameworkCore;

using Ingest.Api.Endpoints;
using Ingest.Api.Infrastructure;
using Ingest.Application.Interfaces;
using Ingest.Application.Services;
using Ingest.Domain.Repositories;
using Ingest.Infrastructure.Adapters;
using Ingest.Infrastructure.Data;
using Ingest.Infrastructure.Kafka;
using Ingest.Infrastructure.Logging;
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

var sourceKafkaBrokers = builder.Configuration["SOURCE_KAFKA_BROKERS"]
    ?? throw new InvalidOperationException(
        "SOURCE_KAFKA_BROKERS is not set. Set it to the sources cluster, for example kafka-sources:9092 (see backend/ingest/.env.example).");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(provider => new LogThrottle(
    TimeSpan.FromSeconds(builder.Configuration.GetValue("LOG_THROTTLE_SECONDS", LogThrottle.DefaultSeconds)),
    provider.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IRawEventPublisher>(_ => new KafkaRawEventPublisher(kafkaBrokers));
builder.Services.AddScoped<IIngestService, IngestService>();

builder.Services.AddHostedService(provider => new ConfigChangesConsumer(
    provider.GetRequiredService<IServiceScopeFactory>(),
    kafkaBrokers,
    provider.GetRequiredService<ILogger<ConfigChangesConsumer>>(),
    provider.GetRequiredService<LogThrottle>()));
builder.Services.AddHostedService(provider => new KafkaSourceManager(
    provider.GetRequiredService<ISourceRegistry>(),
    brokers => new KafkaSourceAdapter(
        brokers,
        provider.GetRequiredService<IRawEventPublisher>(),
        provider.GetRequiredService<TimeProvider>(),
        provider.GetRequiredService<ILogger<KafkaSourceAdapter>>(),
        provider.GetRequiredService<LogThrottle>()),
    sourceKafkaBrokers,
    TimeSpan.FromSeconds(5),
    provider.GetRequiredService<ILogger<KafkaSourceManager>>()));
builder.Services.AddHostedService(provider => new SourceRegistryRefresher(
    provider.GetRequiredService<ISourceRegistry>(),
    TimeSpan.FromSeconds(5),
    provider.GetRequiredService<ILogger<SourceRegistryRefresher>>(),
    provider.GetRequiredService<LogThrottle>()));

// Responses use snake_case like every record in contracts/events.md (event_id).
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower);

// The spec and the UI are for developers: on in Development and in the local Docker run, off in production.
var apiDocsEnabled = builder.Environment.IsDevelopment() || builder.Configuration.GetValue<bool>("ENABLE_SWAGGER");

if (apiDocsEnabled)
{
    builder.Services.AddOpenApi();
}

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

if (apiDocsEnabled)
{
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Ingest v1"));
}

app.MapHealthEndpoints();
app.MapIngestEndpoints();

app.Run();

// Lets the test project reach Program through WebApplicationFactory.
public partial class Program;
