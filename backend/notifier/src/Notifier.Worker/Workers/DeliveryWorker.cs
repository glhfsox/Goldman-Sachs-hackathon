using Microsoft.Extensions.Options;

using Notifier.Application.Interfaces;
using Notifier.Application.Options;
using Notifier.Application.Services;

namespace Notifier.Worker.Workers;

// Keeps up to MaxParallelism sends in flight, each in its own scope (and database connection). It claims only
// as many rows as there are free lanes, so a row's lease starts when its send can start, and a slow provider
// holds up only its own lanes instead of the next claim.
public sealed class DeliveryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DeliveryOptions _options;
    private readonly IDeliverySignal _signal;
    private readonly ILogger<DeliveryWorker> _logger;
    private readonly SemaphoreSlim _lanes;

    public DeliveryWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<DeliveryOptions> options,
        IDeliverySignal signal,
        ILogger<DeliveryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _signal = signal;
        _logger = logger;
        _lanes = new SemaphoreSlim(_options.MaxParallelism, _options.MaxParallelism);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(_options.PollIntervalMilliseconds);
        var most = Math.Min(_options.MaxParallelism, _options.BatchSize);
        while (!stoppingToken.IsCancellationRequested)
        {
            // Wait for one free lane, then take every other lane that is free right now.
            await _lanes.WaitAsync(stoppingToken);
            var free = 1;
            while (free < most && _lanes.Wait(0))
            {
                free++;
            }

            IReadOnlyList<string> ids = [];
            try
            {
                using var scope = _scopeFactory.CreateScope();
                ids = await scope.ServiceProvider.GetRequiredService<IDeliveryService>().ClaimDueAsync(free, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Claiming due notifications failed; retrying after {Interval}", interval);
            }

            if (ids.Count < free)
            {
                _lanes.Release(free - ids.Count);
            }

            foreach (var id in ids)
            {
                _ = DeliverAsync(id, stoppingToken);
            }

            // Fewer rows than free lanes: nothing else is due now. Rows due now wake the loop at once;
            // delayed escalation steps and retries are found by the poll.
            if (ids.Count < free)
            {
                await _signal.WaitAsync(interval, stoppingToken);
            }
        }
    }

    private async Task DeliverAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IDeliveryService>().DeliverAsync(id, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping: the row keeps its lease and is claimed again when it runs out.
        }
        catch (Exception exception)
        {
            // The row keeps its lease and is claimed again when it runs out.
            _logger.LogError(exception, "Delivering notification {NotificationId} failed; it will be retried", id);
        }
        finally
        {
            _lanes.Release();
        }
    }
}
