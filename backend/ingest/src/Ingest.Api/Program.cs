using Microsoft.EntityFrameworkCore;

using Ingest.Api.Endpoints;
using Ingest.Api.Infrastructure;
using Ingest.Application.Interfaces;
using Ingest.Domain.Repositories;
using Ingest.Infrastructure.Data;
using Ingest.Infrastructure.Registry;
using Ingest.Infrastructure.Repositories;

var builder = WebApplication.CreateBuilder(args);

var databaseUrl = builder.Configuration["DATABASE_URL"]
    ?? throw new InvalidOperationException(
        "DATABASE_URL is not set. Set it to postgres://user:password@host:port/database (see backend/ingest/.env.example).");

builder.Services.AddDbContext<IngestDbContext>(options =>
    options.UseNpgsql(DatabaseUrl.ToConnectionString(databaseUrl)));
builder.Services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<IngestDbContext>());
builder.Services.AddScoped<ISourceRepository, SourceRepository>();
builder.Services.AddSingleton<ISourceRegistry, InMemorySourceRegistry>();

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var context = scope.ServiceProvider.GetRequiredService<IngestDbContext>();
    await context.Database.MigrateAsync();
}

await app.Services.GetRequiredService<ISourceRegistry>().RefreshAsync();

app.UseExceptionHandler();

app.MapHealthEndpoints();

app.Run();

// Lets the test project reach Program through WebApplicationFactory.
public partial class Program;
