using System.Net.Http.Headers;
using System.Text;

using AlertApi.Application.Actions;
using AlertApi.Domain.Models;

namespace AlertApi.Infrastructure.Actions;

// Every initial action is an HTTP call to a registered target; a subclass only names the action and its defaults.
// The plugin never retries: retries belong to the run's graph, so each attempt is on the timeline.
public abstract class HttpTargetAction : IActionPlugin
{
    public const string ClientName = "actions";
    public const int OutputLimit = 4096;

    private readonly IHttpClientFactory _clients;

    protected HttpTargetAction(IHttpClientFactory clients)
    {
        _clients = clients;
    }

    public abstract string Name { get; }

    public abstract Risk Risk { get; }

    public abstract string? DefaultTarget { get; }

    public virtual string UrlMethod => "POST";

    public virtual TimeSpan DefaultTimeout => TimeSpan.FromSeconds(10);

    public async Task<ActionResult> RunAsync(ActionRequest request, ActionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        message.Headers.Add("Idempotency-Key", context.IdempotencyKey);

        if (request.Body is not null)
        {
            message.Content = new StringContent(request.Body, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(context.Timeout);

        try
        {
            using var response = await _clients.CreateClient(ClientName).SendAsync(message, timeout.Token);
            var output = Cut(await response.Content.ReadAsStringAsync(timeout.Token));

            return response.IsSuccessStatusCode
                ? ActionResult.Success(output)
                : ActionResult.Failure($"HTTP {(int)response.StatusCode} from {request.Method} {request.Url}", output);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failure($"no answer within {context.Timeout.TotalSeconds:0} s from {request.Method} {request.Url}");
        }
        catch (HttpRequestException exception)
        {
            return ActionResult.Failure($"{request.Method} {request.Url} failed: {exception.Message}");
        }
    }

    private static string Cut(string output)
    {
        return output.Length <= OutputLimit ? output : output[..OutputLimit] + "…[truncated]";
    }
}
