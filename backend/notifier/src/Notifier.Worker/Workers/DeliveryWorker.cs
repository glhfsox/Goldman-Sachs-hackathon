using Microsoft.Extensions.Options;

using Notifier.Application.Options;
using Notifier.Application.Services;

namespace Notifier.Worker.Workers;

public sealed class DeliveryWorker : PollingWorker
{
    private readonly DeliveryOptions _options;

    public DeliveryWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<DeliveryOptions> options,
        ILogger<DeliveryWorker> logger)
        : base(scopeFactory, logger)
    {
        _options = options.Value;
    }

    protected override TimeSpan Interval => TimeSpan.FromMilliseconds(_options.PollIntervalMilliseconds);

    protected override int BatchSize => _options.BatchSize;

    protected override async Task<int> RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var deliveries = services.GetRequiredService<IDeliveryService>();

        return await deliveries.DeliverDueAsync(cancellationToken);
    }
}
