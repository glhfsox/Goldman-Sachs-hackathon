namespace Notifier.Application.Interfaces;

// Wakes the delivery loop of this replica when rows are due now, so they do not wait for the next poll.
// Other replicas still find the rows on their own poll.
public interface IDeliverySignal
{
    void Notify();

    // Completes when Notify was called (since the last wait) or the timeout passes.
    Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}
