using Microsoft.Extensions.Options;

using Notifier.Application.Options;
using Notifier.Application.Services;

namespace Notifier.Worker.Workers;

public sealed class OutboxWorker : PollingWorker
{
    private readonly OutboxOptions _options;

    public OutboxWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<OutboxOptions> options,
        ILogger<OutboxWorker> logger)
        : base(scopeFactory, logger)
    {
        _options = options.Value;
    }

    protected override TimeSpan Interval => TimeSpan.FromMilliseconds(_options.PollIntervalMilliseconds);

    protected override int BatchSize => _options.BatchSize;

    protected override async Task<int> RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var outbox = services.GetRequiredService<IOutboxService>();

        return await outbox.DispatchAsync(cancellationToken);
    }
}
