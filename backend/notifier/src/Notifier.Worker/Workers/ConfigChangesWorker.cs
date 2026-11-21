using Notifier.Application.Constants;
using Notifier.Application.DTOs.Messages;
using Notifier.Application.Services;
using Notifier.Infrastructure.Messaging;

namespace Notifier.Worker.Workers;

public sealed class ConfigChangesWorker : KafkaConsumerWorker<ConfigChangeMessage>
{
    public ConfigChangesWorker(
        IServiceScopeFactory scopeFactory,
        KafkaConsumerFactory consumerFactory,
        ILogger<ConfigChangesWorker> logger)
        : base(scopeFactory, consumerFactory, logger)
    {
    }

    protected override string Topic => Topics.ConfigChanges;

    protected override bool ReplayFromBeginning => true;

    protected override async Task HandleAsync(IServiceProvider services, ConfigChangeMessage message, CancellationToken cancellationToken)
    {
        var configChanges = services.GetRequiredService<IConfigChangeService>();
        await configChanges.ApplyAsync(message, cancellationToken);
    }
}
