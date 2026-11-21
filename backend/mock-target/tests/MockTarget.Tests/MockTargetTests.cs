using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

namespace MockTarget.Tests;

public sealed class MockTargetTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public MockTargetTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task EveryCallIsLogged_AndARepeatedKeyIsAReplayWithTheSameAnswer()
    {
        using var client = _factory.CreateClient();
        await client.DeleteAsync("/_calls");

        var first = await SendAsync(client, HttpMethod.Post, "/workflow-engine/workflows/WF-1/retry", "run-1:retry", "{\"a\":1}");
        var again = await SendAsync(client, HttpMethod.Post, "/workflow-engine/workflows/WF-1/retry", "run-1:retry", "{\"a\":1}");
        var calls = await client.GetFromJsonAsync<JsonElement>("/_calls");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await again.Content.ReadAsStringAsync());
        Assert.Equal(2, calls.GetArrayLength());
        Assert.Equal("/workflow-engine/workflows/WF-1/retry", calls[0].GetProperty("path").GetString());
        Assert.Equal("{\"a\":1}", calls[0].GetProperty("body").GetString());
        Assert.False(calls[0].GetProperty("replay").GetBoolean());
        Assert.True(calls[1].GetProperty("replay").GetBoolean());
    }

    [Fact]
    public async Task FailRule_FailsTheNextMatchingCallsOnly()
    {
        using var client = _factory.CreateClient();
        await client.DeleteAsync("/_calls");
        await client.PostAsJsonAsync("/_fail", new { path_prefix = "/escalation", times = 2, status = 503 });

        var first = await SendAsync(client, HttpMethod.Post, "/escalation/escalations", "k1", null);
        var other = await SendAsync(client, HttpMethod.Get, "/services/health", "k2", null);
        var second = await SendAsync(client, HttpMethod.Post, "/escalation/escalations", "k3", null);
        var third = await SendAsync(client, HttpMethod.Post, "/escalation/escalations", "k4", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string key, string? body)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Idempotency-Key", key);

        if (body is not null)
        {
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request);
    }
}
