namespace Notifier.Worker.Workers;

public abstract class PollingWorker : BackgroundService
{
    private readonly ILogger _logger;

    protected PollingWorker(IServiceScopeFactory scopeFactory, ILogger logger)
    {
        ScopeFactory = scopeFactory;
        _logger = logger;
    }

    protected IServiceScopeFactory ScopeFactory { get; }

    protected abstract TimeSpan Interval { get; }

    protected abstract int BatchSize { get; }

    protected abstract Task<int> RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken);

    // Between rounds; a worker that can be woken early overrides this.
    protected virtual Task WaitAsync(TimeSpan interval, CancellationToken cancellationToken) => Task.Delay(interval, cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var handled = 0;
            try
            {
                using var scope = ScopeFactory.CreateScope();
                handled = await RunOnceAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "{Worker} round failed; retrying after {Interval}", GetType().Name, Interval);
            }

            // A full batch means more rows are waiting, so the next round starts at once.
            if (handled < BatchSize)
            {
                await WaitAsync(Interval, stoppingToken);
            }
        }
    }
}
