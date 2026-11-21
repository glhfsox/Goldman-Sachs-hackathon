using Microsoft.Extensions.Options;

using Notifier.Application.Options;
using Notifier.Application.Services;

namespace Notifier.Worker.Workers;

public sealed class CleanupWorker : PollingWorker
{
    private readonly CleanupOptions _options;

    public CleanupWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<CleanupOptions> options,
        ILogger<CleanupWorker> logger)
        : base(scopeFactory, logger)
    {
        _options = options.Value;
    }

    protected override TimeSpan Interval => TimeSpan.FromMinutes(_options.IntervalMinutes);

    // One delete per table per round, so a round never asks for another straight away.
    protected override int BatchSize => int.MaxValue;

    protected override async Task<int> RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var cleanup = services.GetRequiredService<ICleanupService>();

        return await cleanup.CleanUpAsync(cancellationToken);
    }
}
