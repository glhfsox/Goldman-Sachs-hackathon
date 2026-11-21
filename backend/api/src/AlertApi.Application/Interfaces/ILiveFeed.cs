using System.Net.WebSockets;

namespace AlertApi.Application.Interfaces;

// The browsers connected to this replica. Any replica can push to any browser because every replica reads ui.alerts.
public interface ILiveFeed
{
    // Serves one browser until it disconnects. A team limits the feed to that team's alerts.
    Task RunAsync(WebSocket socket, string? teamId, CancellationToken cancellationToken = default);

    // Sends one ui.alerts message to every connected browser it concerns.
    Task BroadcastAsync(string message, CancellationToken cancellationToken = default);
}
