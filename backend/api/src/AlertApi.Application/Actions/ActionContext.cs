namespace AlertApi.Application.Actions;

public sealed record ActionContext(
    string AlertId,
    string RunId,
    string NodeId,
    string IdempotencyKey,
    string Actor,
    TimeSpan Timeout);
