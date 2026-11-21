using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using AlertApi.Application.Interfaces;
using AlertApi.Application.Runs;

namespace AlertApi.Infrastructure.Runs;

// Several loops per replica, each taking one node at a time. Replicas share the work through the claim in the database.
// An unhandled exception stops the host in .NET 10, so every pass is wrapped.
public sealed class RunExecutorWorker : BackgroundService
{
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RunExecutorOptions _options;
    private readonly ILogger<RunExecutorWorker> _logger;

    public RunExecutorWorker(
        IServiceScopeFactory scopeFactory,
        RunExecutorOptions options,
        ILogger<RunExecutorWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogWarning("Runbook execution is switched off (RUNBOOK_EXECUTION_ENABLED=false); no step will run.");

            return;
        }

        var loops = Enumerable.Range(0, _options.Concurrency).Select(_ => LoopAsync(stoppingToken));

        await Task.WhenAll(loops);
    }

    private async Task LoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = _options.PollInterval;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var executor = scope.ServiceProvider.GetRequiredService<IRunExecutor>();
                var automation = scope.ServiceProvider.GetRequiredService<IRunAutomation>();

                // The same loops also start runs whose cancel window ended; the claim keeps replicas apart.
                var started = await automation.StartDueAsync(stoppingToken);

                if (await executor.ExecuteNextAsync(stoppingToken) || started)
                {
                    delay = TimeSpan.Zero;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "The runbook executor failed; retrying in {Delay}.", ErrorDelay);
                delay = ErrorDelay;
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
