using System.Text.Json;

namespace AlertApi.Application.DTOs.Requests;

public sealed record PutActionTargetRequest(
    string? BaseUrl,
    JsonElement? Origins,
    JsonElement? Operations,
    string? SecretRef);
