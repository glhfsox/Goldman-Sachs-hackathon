namespace AlertApi.Application.DTOs.Requests;

public sealed record CreateSourceRequest(
    string Id,
    string Kind,
    SourceConfigDto? Config);
