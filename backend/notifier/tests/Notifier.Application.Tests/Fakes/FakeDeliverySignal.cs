using Notifier.Application.Interfaces;

namespace Notifier.Application.Tests.Fakes;

public sealed class FakeDeliverySignal : IDeliverySignal
{
    public int Notified { get; private set; }

    public void Notify() => Notified++;

    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
