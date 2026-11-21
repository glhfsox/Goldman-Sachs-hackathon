using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Ingest.Application.Interfaces;

namespace Ingest.Infrastructure.Registry;

// Reloads the in-memory registry from the database. Another replica may have saved a change
// from config.changes, and only polling sees it.
public sealed class SourceRegistryRefresher : BackgroundService
{
    private readonly ISourceRegistry _registry;
    private readonly TimeSpan _interval;
    private readonly ILogger<SourceRegistryRefresher> _logger;

    public SourceRegistryRefresher(
        ISourceRegistry registry,
        TimeSpan interval,
        ILogger<SourceRegistryRefresher> logger)
    {
        _registry = registry;
        _interval = interval;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RefreshAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task RefreshAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _registry.RefreshAsync(stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Keep the last good copy; the next tick tries again.
            _logger.LogWarning(exception, "Cannot refresh the source registry");
        }
    }
}
