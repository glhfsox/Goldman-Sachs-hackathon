namespace AlertApi.Application.DTOs.Responses;

public sealed record TeamMemberResponse(
    string UserId,
    string Name,
    bool OnCall);
