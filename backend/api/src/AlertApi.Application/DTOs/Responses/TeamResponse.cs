namespace AlertApi.Application.DTOs.Responses;

public sealed record TeamResponse(
    string Id,
    string Name,
    IReadOnlyList<TeamMemberResponse> Members);
