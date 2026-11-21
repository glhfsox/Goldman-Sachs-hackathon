using AlertApi.Application.DTOs.Requests;

namespace AlertApi.Application.DTOs.Responses;

public sealed record SourceResponse(
    string Id,
    string Kind,
    SourceConfigDto Config,
    bool Enabled,
    DateTimeOffset UpdatedAt);
