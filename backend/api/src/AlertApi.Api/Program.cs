using Microsoft.EntityFrameworkCore;

using AlertApi.Api.Endpoints;
using AlertApi.Api.Infrastructure;
using AlertApi.Application.Actions;
using AlertApi.Application.Interfaces;
using AlertApi.Application.Runs;
using AlertApi.Application.Serialization;
using AlertApi.Application.Services;
using AlertApi.Domain.Repositories;
using AlertApi.Infrastructure.Actions;
using AlertApi.Infrastructure.Data;
using AlertApi.Infrastructure.Kafka;
using AlertApi.Infrastructure.Outbox;
using AlertApi.Infrastructure.Realtime;
using AlertApi.Infrastructure.Redis;
using AlertApi.Infrastructure.Runs;
using AlertApi.Infrastructure.Queries;
using AlertApi.Infrastructure.Repositories;

var builder = WebApplication.CreateBuilder(args);

var databaseUrl = builder.Configuration["DATABASE_URL"]
    ?? throw new InvalidOperationException(
        "DATABASE_URL is not set. Set it to postgres://user:password@host:port/database (see backend/api/.env.example).");

var kafkaBrokers = builder.Configuration["KAFKA_BROKERS"]
    ?? throw new InvalidOperationException(
        "KAFKA_BROKERS is not set. Set it to the internal cluster, for example kafka:9092 (see backend/api/.env.example).");

var redisUrl = builder.Configuration["REDIS_URL"]
    ?? throw new InvalidOperationException(
        "REDIS_URL is not set. Set it to redis://host:port/0, for example redis://redis:6379/0 (see backend/api/.env.example).");

builder.Services.AddDbContext<ApiDbContext>(options =>
    options.UseNpgsql(DatabaseUrl.ToConnectionString(databaseUrl)));
builder.Services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<ApiDbContext>());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IConfigChangeOutbox, ConfigChangeOutbox>();
builder.Services.AddScoped<IAlertChangeOutbox, AlertChangeOutbox>();
builder.Services.AddScoped<ISourceRepository, SourceRepository>();
builder.Services.AddScoped<ISourceService, SourceService>();
builder.Services.AddScoped<IRuleRepository, RuleRepository>();
builder.Services.AddScoped<IRuleService, RuleService>();
builder.Services.AddScoped<IMappingRepository, MappingRepository>();
builder.Services.AddScoped<IMappingService, MappingService>();
builder.Services.AddScoped<IAlertRepository, AlertRepository>();
builder.Services.AddScoped<IAlertEventRepository, AlertEventRepository>();
builder.Services.AddScoped<INotificationRepository, NotificationRepository>();
builder.Services.AddScoped<ISchemaSampleRepository, SchemaSampleRepository>();
builder.Services.AddScoped<IAlertService, AlertService>();
builder.Services.AddScoped<IActivityService, ActivityService>();
builder.Services.AddScoped<ISchemaSampleService, SchemaSampleService>();
builder.Services.AddScoped<IActivityRecordHandler, ActivityRecordHandler>();
builder.Services.AddScoped<IReferenceDataQueries, ReferenceDataQueries>();
builder.Services.AddScoped<IPreferencesRepository, PreferencesRepository>();
builder.Services.AddScoped<IPreferencesService, PreferencesService>();
builder.Services.AddScoped<IRunbookRepository, RunbookRepository>();
builder.Services.AddScoped<IRunbookService, RunbookService>();
builder.Services.AddScoped<IActionTargetRepository, ActionTargetRepository>();
builder.Services.AddScoped<IActionTargetService, ActionTargetService>();
builder.Services.AddScoped<IActionCatalogProvider, ActionCatalogProvider>();
// Runbook actions: one plugin class and one line here per kind of action (contracts/runbooks.md §3).
builder.Services.AddHttpClient(HttpTargetAction.ClientName);
builder.Services.AddSingleton<IActionPlugin, HttpGetAction>();
builder.Services.AddSingleton<IActionPlugin, RetryWorkflowAction>();
builder.Services.AddSingleton<IActionPlugin, ReloadDbAction>();
builder.Services.AddSingleton<IActionPlugin, RestartServiceAction>();
builder.Services.AddSingleton<IActionPlugin, WebhookAction>();
builder.Services.AddSingleton<IActionPlugin, TicketAction>();
builder.Services.AddSingleton<IActionPlugin, EscalateAction>();
builder.Services.AddSingleton<IActionRegistry, ActionRegistry>();
builder.Services.AddScoped<IRunRepository, RunRepository>();
builder.Services.AddScoped<IRunService, RunService>();
builder.Services.AddSingleton(new RunExecutorOptions
{
    Enabled = builder.Configuration.GetValue("RUNBOOK_EXECUTION_ENABLED", true),
    PollInterval = TimeSpan.FromMilliseconds(builder.Configuration.GetValue("RUNBOOK_POLL_MS", 1000)),
    Lease = TimeSpan.FromSeconds(builder.Configuration.GetValue("RUNBOOK_LEASE_S", 120)),
    Concurrency = builder.Configuration.GetValue("RUNBOOK_CONCURRENCY", 4)
});
builder.Services.AddScoped<IRunExecutor, RunExecutor>();
builder.Services.AddScoped<IRunbookStartRepository, RunbookStartRepository>();
builder.Services.AddScoped<IRunbookStartService, RunbookStartService>();
builder.Services.AddSingleton(new RunAutomationOptions
{
    CancelWindow = TimeSpan.FromSeconds(builder.Configuration.GetValue("RUNBOOK_CANCEL_WINDOW_S", 30)),
    RiskCheck = builder.Configuration.GetValue("RUNBOOK_RISK_CHECK", true)
});
builder.Services.AddScoped<IRunAutomation, RunAutomationService>();

// A separate container can run the executor alone: switch it off in the api replicas with RUNBOOK_EXECUTOR=false.
if (builder.Configuration.GetValue("RUNBOOK_EXECUTOR", true))
{
    builder.Services.AddHostedService<RunExecutorWorker>();
}
builder.Services.AddScoped<IProposalRepository, ProposalRepository>();
builder.Services.AddScoped<IProposalService, ProposalService>();
builder.Services.AddSingleton(new RedisOptions { Url = redisUrl });
builder.Services.AddSingleton<RedisConnection>();
builder.Services.AddSingleton<IUiPublisher, RedisUiPublisher>();
builder.Services.AddSingleton<ILiveFeed, LiveFeed>();
builder.Services.AddScoped<IAlertTeamLookup, AlertTeamLookup>();
builder.Services.AddHostedService<UiAlertsSubscriber>();
builder.Services.AddSingleton(new ActivityConsumerOptions { Brokers = kafkaBrokers });
builder.Services.AddHostedService<ActivityConsumer>();
builder.Services.AddSingleton<IEventPublisher>(_ => new KafkaEventPublisher(kafkaBrokers));
builder.Services.AddHostedService(provider => new OutboxPublisher(
    provider.GetRequiredService<IServiceScopeFactory>(),
    TimeSpan.FromMilliseconds(500),
    TimeSpan.FromSeconds(5),
    provider.GetRequiredService<ILogger<OutboxPublisher>>()));

// Responses use snake_case like every record in contracts/ (first_seen, rule_id).
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.Converters.Add(new UtcTimestampConverter());
});

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
app.UseWebSockets();

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
app.MapAlertEndpoints();
app.MapSchemaSampleEndpoints();
app.MapRunbookEndpoints();
app.MapActionEndpoints();
app.MapRunEndpoints();
app.MapProposalEndpoints();
app.MapRunbookStartEndpoints();
app.MapLiveEndpoints();

app.Run();

// Lets the test project reach Program through WebApplicationFactory.
public partial class Program;
