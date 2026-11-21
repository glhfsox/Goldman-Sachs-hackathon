namespace AlertApi.Application.DTOs.Requests;

public sealed record UpdateSourceRequest(
    string Kind,
    SourceConfigDto? Config,
    bool Enabled);
