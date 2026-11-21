namespace AlertApi.Application.DTOs.Responses;

public sealed record UserResponse(
    string Id,
    string Name,
    string Email,
    string Phone);
