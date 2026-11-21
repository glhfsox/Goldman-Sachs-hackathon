namespace AlertApi.Application.DTOs.Requests;

public sealed record FailSchemaSampleRequest(
    string Status,
    string? Error);
