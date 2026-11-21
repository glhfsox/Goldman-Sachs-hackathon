namespace AlertApi.Application.Actions;

// What a plugin sends, resolved from a step and its target before the run starts; Body is JSON or null.
public sealed record ActionRequest(
    string Method,
    string Url,
    string? Body);
