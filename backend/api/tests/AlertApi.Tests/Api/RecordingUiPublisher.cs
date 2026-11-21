using System.Collections.Concurrent;

using AlertApi.Application.DTOs.Responses;
using AlertApi.Application.Interfaces;

namespace AlertApi.Tests.Api;

public sealed class RecordingUiPublisher : IUiPublisher
{
    public ConcurrentQueue<string> Timelines { get; } = new();

    public ConcurrentQueue<(string Change, AlertResponse Alert)> Alerts { get; } = new();

    public Task PublishTimelineAsync(string alertId, CancellationToken cancellationToken = default)
    {
        Timelines.Enqueue(alertId);

        return Task.CompletedTask;
    }

    public Task PublishAlertAsync(string change, AlertResponse alert, CancellationToken cancellationToken = default)
    {
        Alerts.Enqueue((change, alert));

        return Task.CompletedTask;
    }
}
