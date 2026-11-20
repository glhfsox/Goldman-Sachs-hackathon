namespace Notifier.Worker.Workers;

public abstract class PollingWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;

    protected PollingWorker(IServiceScopeFactory scopeFactory, ILogger logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected abstract TimeSpan Interval { get; }

    protected abstract int BatchSize { get; }

    protected abstract Task<int> RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var handled = 0;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                handled = await RunOnceAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "{Worker} round failed; retrying after {Interval}", GetType().Name, Interval);
            }

            // A full batch means more rows are waiting, so the next round starts at once.
            if (handled < BatchSize)
            {
                await Task.Delay(Interval, stoppingToken);
            }
        }
    }
}
