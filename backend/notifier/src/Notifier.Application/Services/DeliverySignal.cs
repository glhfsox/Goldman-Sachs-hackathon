using Notifier.Application.Interfaces;

namespace Notifier.Application.Services;

public sealed class DeliverySignal : IDeliverySignal
{
    // At most one pending wake-up: many Notify calls before the next wait collapse into one round.
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled.
        }
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await _signal.WaitAsync(timeout, cancellationToken);
    }
}
