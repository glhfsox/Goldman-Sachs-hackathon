using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using AlertApi.Infrastructure.Data;
using AlertApi.Tests.Infrastructure;

namespace AlertApi.Tests.Api;

[Trait("Category", "Integration")]
public sealed class RuleAndMappingEndpointTests : IClassFixture<PostgresFixture>
{
    private const string Rule = """
        {"id": "rates-large", "team_id": "rates",
         "when": {"all": [{"field": "type", "op": "glob", "value": "trade.rejected*"}]},
         "route": {"recipients": [{"team": "rates"}], "channels": ["ui"]}}
        """;

    private const string Mapping = """
        {"id": "trade-v1", "status": "active", "origin": "llm", "priority": 100,
         "match": {"all": [{"field": "raw.meta.schema", "op": "glob", "value": "trade*"}]},
         "fields": {"type": {"path": "meta.schema"}}}
        """;

    private readonly PostgresFixture _postgres;

    public RuleAndMappingEndpointTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task PostRule_ThenGetByTeam_ReturnsItAndQueuesOneConfigRecord()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();

        var created = await client.PostAsync("/api/rules", Json(Rule));
        var rates = await client.GetFromJsonAsync<JsonElement>("/api/rules?team=rates");
        var other = await client.GetFromJsonAsync<JsonElement>("/api/rules?team=pricing");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("rates-large", Assert.Single(rates.EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal(0, other.GetArrayLength());
        await using var scope = factory.Services.CreateAsyncScope();
        var record = await scope.ServiceProvider.GetRequiredService<ApiDbContext>().Outbox
            .SingleAsync(message => message.Key == "rule:rates-large");
        var payload = JsonDocument.Parse(record.Payload).RootElement;
        Assert.Equal("rule", payload.GetProperty("kind").GetString());
        Assert.Equal("rates", payload.GetProperty("body").GetProperty("team_id").GetString());
    }

    [Fact]
    public async Task PostRule_WithUnknownOperator_Returns422WithThePath()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/rules", Json(Rule.Replace("\"glob\"", "\"gtt\"")));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("invalid_rule", body.GetProperty("error").GetString());
        Assert.Equal("when.all[0].op", body.GetProperty("details")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Rule_DuplicateAndUnknownAndMismatchedId_AreRejected()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();
        await client.PostAsync("/api/rules", Json(Rule));

        var duplicate = await client.PostAsync("/api/rules", Json(Rule));
        var unknown = await client.PutAsync("/api/rules/nope", Json(Rule.Replace("rates-large", "nope")));
        var mismatch = await client.PutAsync("/api/rules/rates-large", Json(Rule.Replace("rates-large", "other")));
        var update = await client.PutAsync("/api/rules/rates-large", Json(Rule.Replace("\"ui\"", "\"email\"")));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatch.StatusCode);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
    }

    [Fact]
    public async Task PutMapping_CreatesThenReplaces_AndInvalidOneIs422()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();

        var created = await client.PutAsync("/api/mappings/trade-v1", Json(Mapping));
        var replaced = await client.PutAsync("/api/mappings/trade-v1", Json(Mapping.Replace("\"active\"", "\"disabled\"")));
        var invalid = await client.PutAsync("/api/mappings/trade-v1", Json(Mapping.Replace("\"llm\"", "\"bot\"")));
        var list = await client.GetFromJsonAsync<JsonElement>("/api/mappings");

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal("invalid_mapping", (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal("disabled", Assert.Single(list.EnumerateArray()).GetProperty("status").GetString());
    }

    private static StringContent Json(string json)
    {
        return new StringContent(json, System.Text.Encoding.UTF8, "application/json");
    }
}
