using AlertApi.Domain.Models;

namespace AlertApi.Application.Actions;

public sealed record ActionDescriptor(
    string Name,
    Risk Risk,
    string? DefaultTarget,
    string UrlMethod,
    int TimeoutSeconds);
