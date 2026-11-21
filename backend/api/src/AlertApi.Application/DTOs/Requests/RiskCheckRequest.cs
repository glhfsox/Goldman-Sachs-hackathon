namespace AlertApi.Application.DTOs.Requests;

public sealed record RiskCheckRequest(
    string? Verdict,
    double? Confidence,
    IReadOnlyList<string>? Reasons);
