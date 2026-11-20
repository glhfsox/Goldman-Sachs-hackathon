using Ingest.Api.Endpoints;
using Ingest.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();

app.MapHealthEndpoints();

app.Run();

// Lets the test project reach Program through WebApplicationFactory.
public partial class Program;
