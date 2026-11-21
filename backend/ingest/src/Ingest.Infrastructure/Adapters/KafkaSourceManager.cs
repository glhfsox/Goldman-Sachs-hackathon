using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Ingest.Application.Interfaces;

namespace Ingest.Infrastructure.Adapters;

// Keeps one adapter per sources cluster running and tells it which topics to read.
public sealed class KafkaSourceManager : BackgroundService
{
    private readonly ISourceRegistry _registry;
    private readonly Func<string, ISourceAdapter> _adapterFactory;
    private readonly string _defaultBrokers;
    private readonly TimeSpan _interval;
    private readonly ILogger<KafkaSourceManager> _logger;
    private readonly Dictionary<string, RunningAdapter> _running = [];

    public KafkaSourceManager(
        ISourceRegistry registry,
        Func<string, ISourceAdapter> adapterFactory,
        string defaultBrokers,
        TimeSpan interval,
        ILogger<KafkaSourceManager> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultBrokers);

        _registry = registry;
        _adapterFactory = adapterFactory;
        _defaultBrokers = defaultBrokers;
        _interval = interval;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);

        try
        {
            do
            {
                Reconcile(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            await StopAllAsync();
        }
    }

    private void Reconcile(CancellationToken stoppingToken)
    {
        var plan = KafkaSourcePlanner.Plan(_registry.GetEnabled(), _defaultBrokers, _logger);

        foreach (var (brokers, topics) in plan)
        {
            if (!_running.TryGetValue(brokers, out var running))
            {
                var adapter = _adapterFactory(brokers);
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                running = new RunningAdapter(adapter, cancellation, adapter.RunAsync(cancellation.Token));
                _running[brokers] = running;
                _logger.LogInformation("Started the Kafka source adapter for {Brokers}", brokers);
            }

            running.Adapter.SetSources(topics);
        }

        // No enabled source left on this cluster: stop reading it. The task ends on its own and is not awaited here.
        foreach (var brokers in _running.Keys.Where(brokers => !plan.ContainsKey(brokers)).ToList())
        {
            var running = _running[brokers];
            running.Adapter.SetSources(new Dictionary<string, string>());
            _logger.LogInformation("No enabled source reads {Brokers} any more; its adapter reads nothing", brokers);
        }
    }

    private async Task StopAllAsync()
    {
        foreach (var running in _running.Values)
        {
            await running.Cancellation.CancelAsync();
        }

        foreach (var running in _running.Values)
        {
            try
            {
                await running.Task;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "A Kafka source adapter stopped with an error");
            }

            running.Cancellation.Dispose();
        }

        _running.Clear();
    }

    private sealed record RunningAdapter(ISourceAdapter Adapter, CancellationTokenSource Cancellation, Task Task);
}
