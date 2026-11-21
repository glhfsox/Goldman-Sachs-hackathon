using Microsoft.Extensions.Options;

using Notifier.Application.Interfaces;
using Notifier.Application.Options;
using Notifier.Application.Services;

namespace Notifier.Worker.Workers;

// Claims a batch, then sends it in parallel: one scope (and database connection) per notification,
// so a slow provider holds up only its own sends.
public sealed class DeliveryWorker : PollingWorker
{
    private readonly DeliveryOptions _options;
    private readonly IDeliverySignal _signal;
    private readonly ILogger<DeliveryWorker> _logger;

    public DeliveryWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<DeliveryOptions> options,
        IDeliverySignal signal,
        ILogger<DeliveryWorker> logger)
        : base(scopeFactory, logger)
    {
        _options = options.Value;
        _signal = signal;
        _logger = logger;
    }

    protected override TimeSpan Interval => TimeSpan.FromMilliseconds(_options.PollIntervalMilliseconds);

    protected override int BatchSize => _options.BatchSize;

    // Rows due now wake the loop at once; delayed escalation steps and retries are found by the poll.
    protected override Task WaitAsync(TimeSpan interval, CancellationToken cancellationToken) =>
        _signal.WaitAsync(interval, cancellationToken);

    protected override async Task<int> RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var ids = await services.GetRequiredService<IDeliveryService>().ClaimDueAsync(cancellationToken);

        var parallel = new ParallelOptions
        {
            MaxDegreeOfParallelism = _options.MaxParallelism,
            CancellationToken = cancellationToken
        };
        await Parallel.ForEachAsync(ids, parallel, async (id, token) =>
        {
            try
            {
                using var scope = ScopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IDeliveryService>().DeliverAsync(id, token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The row keeps its lease and is claimed again when it runs out.
                _logger.LogError(exception, "Delivering notification {NotificationId} failed; it will be retried", id);
            }
        });

        return ids.Count;
    }
}
