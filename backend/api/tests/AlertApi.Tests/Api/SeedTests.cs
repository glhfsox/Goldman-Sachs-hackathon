using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using AlertApi.Infrastructure.Data;
using AlertApi.Tests.Infrastructure;

namespace AlertApi.Tests.Api;

[Trait("Category", "Integration")]
public sealed class SeedTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public SeedTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task AFreshDatabase_HoldsTheBriefsMappingsAndRules_QueuedOnConfigChanges()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();

        var mappings = await client.GetFromJsonAsync<JsonElement>("/api/mappings");
        var rules = await client.GetFromJsonAsync<JsonElement>("/api/rules");
        await using var scope = factory.Services.CreateAsyncScope();
        var keys = await scope.ServiceProvider.GetRequiredService<ApiDbContext>().Outbox
            .Where(message => message.Key.StartsWith("mapping:") || message.Key.StartsWith("rule:"))
            .Select(message => message.Key)
            .ToListAsync();

        Assert.Equal(
            ["alertmanager", "pricing-logs", "trade-rejected-v1", "workflow-failed"],
            mappings.EnumerateArray().Select(mapping => mapping.GetProperty("id").GetString()!).Order());
        Assert.Equal(
            ["anna-payments-critical", "pricing-engine-errors", "rates-large-rejections", "settlements-workflow-failed"],
            rules.EnumerateArray().Select(rule => rule.GetProperty("id").GetString()!).Order());
        Assert.Equal(8, keys.Distinct().Count());
        Assert.Contains("rule:settlements-workflow-failed", keys);
        Assert.Contains("mapping:workflow-failed", keys);
    }

    [Fact]
    public async Task EverySeededMappingAndRule_PassesTheApisOwnValidation()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();

        var mappings = await client.GetFromJsonAsync<JsonElement>("/api/mappings");
        var rules = await client.GetFromJsonAsync<JsonElement>("/api/rules");

        foreach (var mapping in mappings.EnumerateArray())
        {
            var response = await client.PutAsync($"/api/mappings/{mapping.GetProperty("id").GetString()}", Json(mapping));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        foreach (var rule in rules.EnumerateArray())
        {
            var response = await client.PutAsync($"/api/rules/{rule.GetProperty("id").GetString()}", Json(rule));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    private static StringContent Json(JsonElement body)
    {
        return new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");
    }
}
