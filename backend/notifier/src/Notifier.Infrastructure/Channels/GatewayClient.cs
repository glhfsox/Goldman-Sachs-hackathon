using System.Net;
using System.Net.Http.Json;

using Notifier.Application.DTOs.Channels;
using Notifier.Application.Serialization;

namespace Notifier.Infrastructure.Channels;

// Shared HTTP call for phone-number channels (SMS, call). A real provider (Twilio, Vonage) replaces
// the URL and, at most, this request shape.
public sealed class GatewayClient
{
    private readonly HttpClient _httpClient;

    public GatewayClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<SendResult> PostAsync(string url, GatewayRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(url, request, NotifierJson.Options, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<GatewayResponse>(NotifierJson.Options, cancellationToken);

                return SendResult.Sent(body?.Id);
            }

            var status = (int)response.StatusCode;
            if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || status >= 500)
            {
                return SendResult.Retry($"Gateway {url} returned {status}.");
            }

            return SendResult.Failed($"Gateway {url} rejected the message with {status}.");
        }
        catch (HttpRequestException exception)
        {
            return SendResult.Retry($"Gateway {url} is unavailable: {exception.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SendResult.Retry($"Gateway {url} timed out.");
        }
    }
}
