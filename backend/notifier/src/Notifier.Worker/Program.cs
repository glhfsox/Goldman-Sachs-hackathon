using Microsoft.EntityFrameworkCore;

using Notifier.Application.Interfaces;
using Notifier.Application.Options;
using Notifier.Application.Services;
using Notifier.Domain.Repositories;
using Notifier.Domain.Services;
using Notifier.Infrastructure.Channels;
using Notifier.Infrastructure.Data;
using Notifier.Infrastructure.Messaging;
using Notifier.Infrastructure.Repositories;
using Notifier.Infrastructure.Time;
using Notifier.Worker.Workers;

var builder = Host.CreateApplicationBuilder(args);

// The env names of contracts/services.md override the option sections they belong to.
MapEnvironment(builder.Configuration, new Dictionary<string, string>
{
    ["KAFKA_BROKERS"] = "Kafka:Brokers",
    ["PUBLIC_BASE_URL"] = "Delivery:PublicBaseUrl",
    ["SMTP_HOST"] = "Channels:SmtpHost",
    ["SMTP_PORT"] = "Channels:SmtpPort",
    ["SMS_GATEWAY_URL"] = "Channels:SmsGatewayUrl",
    ["CALL_GATEWAY_URL"] = "Channels:CallGatewayUrl"
});

// Notifier's own database (contracts/storage.md: one Postgres per service).
var databaseUrl = builder.Configuration["DATABASE_URL"]
    ?? throw new InvalidOperationException(
        "DATABASE_URL is not set. Point it at Notifier's own database, e.g. postgres://alerts:alerts@localhost:5435/notifier.");
if (string.IsNullOrWhiteSpace(builder.Configuration["Kafka:Brokers"]))
{
    throw new InvalidOperationException("KAFKA_BROKERS is not set, e.g. localhost:9092.");
}

builder.Services.AddOptions<KafkaOptions>().Bind(builder.Configuration.GetSection(KafkaOptions.SectionName)).ValidateOnStart();
builder.Services.AddOptions<DeliveryOptions>().Bind(builder.Configuration.GetSection(DeliveryOptions.SectionName))
    .Validate(options => options.BatchSize > 0 && options.MaxAttempts > 0 && options.MaxParallelism > 0,
        "Delivery:BatchSize, Delivery:MaxAttempts and Delivery:MaxParallelism must be positive.")
    .ValidateOnStart();
builder.Services.AddOptions<EscalationOptions>().Bind(builder.Configuration.GetSection(EscalationOptions.SectionName))
    .Validate(options => options.PushDelayMinutes >= 0 && options.OnCallCallDelayMinutes >= 0, "Escalation delays cannot be negative.")
    .ValidateOnStart();
builder.Services.AddOptions<OutboxOptions>().Bind(builder.Configuration.GetSection(OutboxOptions.SectionName))
    .Validate(options => options.BatchSize > 0 && options.MaxAttempts > 0, "Outbox:BatchSize and Outbox:MaxAttempts must be positive.")
    .ValidateOnStart();
builder.Services.AddOptions<ConfigCacheOptions>().Bind(builder.Configuration.GetSection(ConfigCacheOptions.SectionName))
    .Validate(options => options.CacheSeconds >= 0, "ConfigCache:CacheSeconds cannot be negative.")
    .ValidateOnStart();
builder.Services.AddOptions<CleanupOptions>().Bind(builder.Configuration.GetSection(CleanupOptions.SectionName))
    .Validate(options => options.IntervalMinutes > 0, "Cleanup:IntervalMinutes must be positive.")
    .ValidateOnStart();
builder.Services.AddOptions<ChannelOptions>().Bind(builder.Configuration.GetSection(ChannelOptions.SectionName))
    .Validate(options => options.Enabled.Length > 0, "Channels:Enabled must list at least one channel, e.g. [\"ui\", \"email\", \"sms\", \"call\"].")
    .Validate(options => options.SmtpTimeoutSeconds > 0, "Channels:SmtpTimeoutSeconds must be positive.")
    .ValidateOnStart();

builder.Services.AddDbContext<NotifierDbContext>(options =>
    options.UseNpgsql(PostgresUrl.ToConnectionString(databaseUrl)));
builder.Services.AddScoped<INotificationRepository, NotificationRepository>();
builder.Services.AddScoped<IAlertRepository, AlertRepository>();
builder.Services.AddScoped<IConfigRepository, ConfigRepository>();
builder.Services.AddScoped<IRateCounterRepository, RateCounterRepository>();
builder.Services.AddScoped<IOutboxRepository, OutboxRepository>();
builder.Services.AddScoped<IProcessedChangeRepository, ProcessedChangeRepository>();

builder.Services.AddSingleton<EscalationLadder>();
builder.Services.AddSingleton<DeliveryGate>();
builder.Services.AddSingleton<FallbackPolicy>();
builder.Services.AddSingleton<RecipientResolver>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IMessageRenderService, MessageRenderService>();
builder.Services.AddSingleton<IConfigSnapshotCache, ConfigSnapshotCache>();
builder.Services.AddSingleton<IDeliverySignal, DeliverySignal>();
builder.Services.AddScoped<ITimelineService, TimelineService>();
builder.Services.AddScoped<IAlertChangeService, AlertChangeService>();
builder.Services.AddScoped<IDeliveryService, DeliveryService>();
builder.Services.AddScoped<IConfigChangeService, ConfigChangeService>();
builder.Services.AddScoped<IOutboxService, OutboxService>();
builder.Services.AddScoped<ICleanupService, CleanupService>();

builder.Services.AddSingleton<IEventPublisher, KafkaEventPublisher>();
builder.Services.AddSingleton<KafkaConsumerFactory>();

// A channel is one IChannelAdapter registration here plus its name in Channels:Enabled (contracts/runbooks.md §3).
builder.Services.AddHttpClient<GatewayClient>(client =>
    client.Timeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("Channels:GatewayTimeoutSeconds", 2)));
builder.Services.AddSingleton<SmtpConnectionPool>();
builder.Services.AddScoped<IChannelAdapter, UiChannel>();
builder.Services.AddScoped<IChannelAdapter, EmailChannel>();
builder.Services.AddScoped<IChannelAdapter, SmsChannel>();
builder.Services.AddScoped<IChannelAdapter, CallChannel>();
builder.Services.AddScoped<IChannelAdapterRegistry, ChannelAdapterRegistry>();

builder.Services.AddHostedService<AlertChangesWorker>();
builder.Services.AddHostedService<DeliveryWorker>();
builder.Services.AddHostedService<ConfigChangesWorker>();
builder.Services.AddHostedService<OutboxWorker>();
builder.Services.AddHostedService<CleanupWorker>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<NotifierDbContext>();
    await context.Database.MigrateAsync();
}

await host.RunAsync();

static void MapEnvironment(IConfiguration configuration, Dictionary<string, string> mappings)
{
    foreach (var mapping in mappings)
    {
        if (configuration[mapping.Key] is { Length: > 0 } value)
        {
            configuration[mapping.Value] = value;
        }
    }
}
