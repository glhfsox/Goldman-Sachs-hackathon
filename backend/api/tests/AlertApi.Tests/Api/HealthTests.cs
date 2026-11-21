using System.Net;
using System.Text.Json;

using AlertApi.Tests.Infrastructure;

namespace AlertApi.Tests.Api;

[Trait("Category", "Integration")]
public sealed class HealthTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public HealthTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task Health_AfterMigrating_ReturnsOk()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", body.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(true, HttpStatusCode.OK)]
    [InlineData(false, HttpStatusCode.NotFound)]
    public async Task OpenApiSpec_IsServedOnlyWithTheFlagInProduction(bool enableSwagger, HttpStatusCode expected)
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(expected, response.StatusCode);
    }
}
