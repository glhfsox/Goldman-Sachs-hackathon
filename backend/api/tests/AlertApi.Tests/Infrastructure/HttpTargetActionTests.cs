using System.Net;

using AlertApi.Application.Actions;
using AlertApi.Infrastructure.Actions;

namespace AlertApi.Tests.Infrastructure;

public sealed class HttpTargetActionTests
{
    private static readonly ActionContext Context = new("alert-1", "run-1", "retry", "run-1:retry", "anna", TimeSpan.FromSeconds(2));

    [Fact]
    public async Task SendsTheRequestWithItsIdempotencyKeyAndBody()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") }));
        var action = new EscalateAction(new SingleClientFactory(handler));

        var result = await action.RunAsync(
            new ActionRequest("POST", "http://mock-target:8003/escalation/escalations", "{\"team\":\"dba\"}"),
            Context);

        Assert.True(result.Ok);
        Assert.Equal("{\"ok\":true}", result.Output);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("run-1:retry", request.Headers.GetValues("Idempotency-Key").Single());
        Assert.Equal("{\"team\":\"dba\"}", handler.Bodies.Single());
    }

    [Fact]
    public async Task ANon2xxAnswer_IsAFailureWithTheStatus()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("down") }));

        var result = await new RetryWorkflowAction(new SingleClientFactory(handler))
            .RunAsync(new ActionRequest("POST", "http://t/workflows/WF-1/retry", null), Context);

        Assert.False(result.Ok);
        Assert.Contains("HTTP 503", result.Error);
        Assert.Equal("down", result.Output);
    }

    [Fact]
    public async Task NoAnswerWithinTheTimeout_IsAFailureNotAnException()
    {
        var handler = new RecordingHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var result = await new HttpGetAction(new SingleClientFactory(handler))
            .RunAsync(new ActionRequest("GET", "http://t/health", null), Context with { Timeout = TimeSpan.FromMilliseconds(100) });

        Assert.False(result.Ok);
        Assert.Contains("no answer within", result.Error);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _answer;

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer)
        {
            _answer = answer;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));

            return await _answer(request, cancellationToken);
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SingleClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name)
        {
            return new HttpClient(_handler, disposeHandler: false);
        }
    }
}
