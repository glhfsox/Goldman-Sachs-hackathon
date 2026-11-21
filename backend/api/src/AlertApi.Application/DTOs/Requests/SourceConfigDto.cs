namespace AlertApi.Application.DTOs.Requests;

public sealed record SourceConfigDto(
    string? Topic,
    string? Brokers);
