using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Ingest.Tests.Infrastructure;

namespace Ingest.Tests.Api;

// The spec and Swagger UI are for developers: on with ENABLE_SWAGGER or in Development, off in production.
[Trait("Category", "Integration")]
public sealed class ApiDocsTests : IClassFixture<PostgresFixture>, IClassFixture<KafkaFixture>
{
    private readonly PostgresFixture _postgres;
    private readonly KafkaFixture _kafka;

    public ApiDocsTests(PostgresFixture postgres, KafkaFixture kafka)
    {
        _postgres = postgres;
        _kafka = kafka;
    }

    [Fact]
    public async Task OpenApiSpec_WithFlag_DescribesBothRoutesAndTheErrors()
    {
        using var factory = CreateFactory("Production", enableSwagger: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");
        var spec = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paths = spec.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/health", out _));
        var responses = paths.GetProperty("/ingest/{sourceId}").GetProperty("post").GetProperty("responses");
        Assert.Equal(["202", "404", "413", "503"], responses.EnumerateObject().Select(code => code.Name).Order());
    }

    [Fact]
    public async Task SwaggerUi_WithFlag_IsServedAtSwagger()
    {
        using var factory = CreateFactory("Production", enableSwagger: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger/index.html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/openapi/v1.json")]
    [InlineData("/swagger/index.html")]
    public async Task Docs_InProductionWithoutFlag_AreNotServed(string path)
    {
        using var factory = CreateFactory("Production", enableSwagger: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OpenApiSpec_InDevelopmentWithoutFlag_IsServed()
    {
        using var factory = CreateFactory("Development", enableSwagger: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private WebApplicationFactory<Program> CreateFactory(string environment, bool enableSwagger)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("DATABASE_URL", _postgres.DatabaseUrl);
            builder.UseSetting("KAFKA_BROKERS", _kafka.Brokers);
            builder.UseSetting("SOURCE_KAFKA_BROKERS", _kafka.Brokers);
            builder.UseSetting("ENABLE_SWAGGER", enableSwagger ? "true" : "false");
        });
    }
}
