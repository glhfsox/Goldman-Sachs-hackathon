namespace AlertApi.Application.DTOs.Responses;

public sealed record RunProgressStepResponse(
    string Node,
    string Status);
