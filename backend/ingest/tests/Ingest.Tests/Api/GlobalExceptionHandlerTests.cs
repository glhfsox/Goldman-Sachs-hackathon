using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using Ingest.Api.Infrastructure;
using Ingest.Application.Exceptions;

namespace Ingest.Tests.Api;

public sealed class GlobalExceptionHandlerTests
{
    [Fact]
    public async Task TryHandleAsync_AppException_UsesItsOwnStatus()
    {
        var (status, body) = await HandleAsync(new SampleNotFoundException(), Environments.Production);

        Assert.Equal(404, status);
        Assert.Equal("Sample was not found.", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task TryHandleAsync_UnknownException_HidesMessageOutsideDevelopment()
    {
        var (status, body) = await HandleAsync(new InvalidOperationException("secret"), Environments.Production);

        Assert.Equal(500, status);
        Assert.DoesNotContain("secret", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task TryHandleAsync_UnknownException_ShowsMessageInDevelopment()
    {
        var (_, body) = await HandleAsync(new InvalidOperationException("secret"), Environments.Development);

        Assert.Equal("secret", body.GetProperty("detail").GetString());
    }

    private static async Task<(int Status, JsonElement Body)> HandleAsync(Exception exception, string environmentName)
    {
        var handler = new GlobalExceptionHandler(
            new TestEnvironment(environmentName),
            NullLogger<GlobalExceptionHandler>.Instance);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.True(handled);
        context.Response.Body.Position = 0;
        var body = await JsonDocument.ParseAsync(context.Response.Body);

        return (context.Response.StatusCode, body.RootElement);
    }

    private sealed class SampleNotFoundException : NotFoundException
    {
        public SampleNotFoundException()
            : base("Sample was not found.")
        {
        }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public TestEnvironment(string environmentName)
        {
            EnvironmentName = environmentName;
        }

        public string EnvironmentName { get; set; }

        public string ApplicationName { get; set; } = "Ingest.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
