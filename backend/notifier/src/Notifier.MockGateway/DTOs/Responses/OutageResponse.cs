namespace Notifier.MockGateway.DTOs.Responses;

public sealed record OutageResponse(
    bool Active,
    DateTimeOffset? Until);
