namespace AlertApi.Application.DTOs.Requests;

public sealed record CreateRunbookRequest(
    string? TeamId,
    string? Title,
    string? Format,
    string? Source);
