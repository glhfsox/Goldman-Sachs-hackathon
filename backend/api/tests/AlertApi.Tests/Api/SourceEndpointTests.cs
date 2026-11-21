using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using AlertApi.Tests.Infrastructure;

namespace AlertApi.Tests.Api;

[Trait("Category", "Integration")]
public sealed class SourceEndpointTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public SourceEndpointTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task Get_OnFreshDatabase_ListsTheFourSeededSources()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();

        var sources = await client.GetFromJsonAsync<JsonElement>("/api/sources");

        Assert.Equal(
            ["alertmanager-http", "pricing-logs-http", "trades-kafka", "workflows-kafka"],
            sources.EnumerateArray().Select(source => source.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task PostThenPut_CreatesAndDisablesASource()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync(
            "/api/sources",
            new { id = "orders-kafka", kind = "kafka", config = new { topic = "source.orders" } });
        var updated = await client.PutAsJsonAsync(
            "/api/sources/orders-kafka",
            new { kind = "kafka", config = new { topic = "source.orders" }, enabled = false });
        var body = await updated.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/api/sources/orders-kafka", created.Headers.Location?.AbsolutePath);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.False(body.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Post_KafkaWithoutTopic_Returns422InTheContractShape()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/sources", new { id = "orders-kafka", kind = "kafka" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("invalid_source", body.GetProperty("error").GetString());
        Assert.Equal("config.topic", body.GetProperty("details")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Post_ExistingId_Returns409AndUnknownPutReturns404()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();

        var duplicate = await client.PostAsJsonAsync("/api/sources", new { id = "alertmanager-http", kind = "http" });
        var missing = await client.PutAsJsonAsync("/api/sources/nope", new { kind = "http", enabled = true });

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
