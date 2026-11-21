using MockTarget;

// The system runbook actions call in the demo (contracts/services.md): it answers every path, logs each call with
// its Idempotency-Key, and can be told to fail. Nothing is persisted; a restart forgets the log.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<CallLog>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower);

var app = builder.Build();

app.MapGet("/_calls", (CallLog log) => TypedResults.Ok(log.All()));

app.MapDelete("/_calls", (CallLog log) =>
{
    log.Clear();

    return TypedResults.NoContent();
});

app.MapPost("/_fail", (FailRule rule, CallLog log) =>
{
    if (string.IsNullOrEmpty(rule.PathPrefix) || rule.Times < 1 || rule.Status is < 400 or > 599)
    {
        return Results.BadRequest(new { error = "need path_prefix, times >= 1 and a 4xx/5xx status" });
    }

    log.AddFailure(rule);

    return Results.NoContent();
});

app.Map("/{**path}", async (HttpContext context, CallLog log, TimeProvider time) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync(context.RequestAborted);
    var key = context.Request.Headers.TryGetValue("Idempotency-Key", out var value) ? value.ToString() : null;
    var response = log.Handle(
        time.GetUtcNow(),
        context.Request.Method,
        context.Request.Path.Value ?? "/",
        context.Request.QueryString.Value ?? string.Empty,
        body,
        key);

    app.Logger.LogInformation(
        "{Method} {Path} -> {Status} (key {Key})", context.Request.Method, context.Request.Path, response.Status, key ?? "none");

    return Results.Content(response.Body, "application/json", statusCode: response.Status);
});

app.Run();

// Lets the test project reach Program through WebApplicationFactory.
public partial class Program;
