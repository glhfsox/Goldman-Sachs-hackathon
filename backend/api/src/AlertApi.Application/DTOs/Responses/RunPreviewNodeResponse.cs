using System.Text.Json;

namespace AlertApi.Application.DTOs.Responses;

public sealed record RunPreviewNodeResponse(
    string Id,
    string Text,
    string? Action,
    string Risk,
    JsonElement? Params,
    IReadOnlyList<string> Missing);
