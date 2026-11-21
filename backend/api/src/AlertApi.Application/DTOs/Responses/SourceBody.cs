using AlertApi.Application.DTOs.Requests;

namespace AlertApi.Application.DTOs.Responses;

// The "body" of a config.changes record of kind "source" (contracts/events.md section 6).
public sealed record SourceBody(
    string Id,
    string Kind,
    SourceConfigDto Config,
    bool Enabled);
