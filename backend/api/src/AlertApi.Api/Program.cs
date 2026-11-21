using Microsoft.EntityFrameworkCore;

using AlertApi.Api.Endpoints;
using AlertApi.Api.Infrastructure;
using AlertApi.Application.Interfaces;
using AlertApi.Application.Services;
using AlertApi.Domain.Repositories;
using AlertApi.Infrastructure.Data;
using AlertApi.Infrastructure.Kafka;
using AlertApi.Infrastructure.Outbox;
using AlertApi.Infrastructure.Queries;
using AlertApi.Infrastructure.Repositories;

var builder = WebApplication.CreateBuilder(args);

var databaseUrl = builder.Configuration["DATABASE_URL"]
    ?? throw new InvalidOperationException(
        "DATABASE_URL is not set. Set it to postgres://user:password@host:port/database (see backend/api/.env.example).");

var kafkaBrokers = builder.Configuration["KAFKA_BROKERS"]
    ?? throw new InvalidOperationException(
        "KAFKA_BROKERS is not set. Set it to the internal cluster, for example kafka:9092 (see backend/api/.env.example).");

builder.Services.AddDbContext<ApiDbContext>(options =>
    options.UseNpgsql(DatabaseUrl.ToConnectionString(databaseUrl)));
builder.Services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<ApiDbContext>());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IConfigChangeOutbox, ConfigChangeOutbox>();
builder.Services.AddScoped<ISourceRepository, SourceRepository>();
builder.Services.AddScoped<ISourceService, SourceService>();
builder.Services.AddScoped<IRuleRepository, RuleRepository>();
builder.Services.AddScoped<IRuleService, RuleService>();
builder.Services.AddScoped<IMappingRepository, MappingRepository>();
builder.Services.AddScoped<IMappingService, MappingService>();
builder.Services.AddScoped<IReferenceDataQueries, ReferenceDataQueries>();
builder.Services.AddScoped<IPreferencesRepository, PreferencesRepository>();
builder.Services.AddScoped<IPreferencesService, PreferencesService>();
builder.Services.AddSingleton<IEventPublisher>(_ => new KafkaEventPublisher(kafkaBrokers));
builder.Services.AddHostedService(provider => new OutboxPublisher(
    provider.GetRequiredService<IServiceScopeFactory>(),
    TimeSpan.FromMilliseconds(500),
    TimeSpan.FromSeconds(5),
    provider.GetRequiredService<ILogger<OutboxPublisher>>()));

// Responses use snake_case like every record in contracts/ (first_seen, rule_id).
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
    var context = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
    await context.Database.MigrateAsync();
}

app.UseExceptionHandler();

if (apiDocsEnabled)
{
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Alert API v1"));
}

app.MapHealthEndpoints();
app.MapSourceEndpoints();
app.MapRuleEndpoints();
app.MapMappingEndpoints();
app.MapTeamEndpoints();
app.MapUserEndpoints();

app.Run();

// Lets the test project reach Program through WebApplicationFactory.
public partial class Program;
