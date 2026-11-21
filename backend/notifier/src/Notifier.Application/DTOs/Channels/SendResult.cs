namespace Notifier.Application.DTOs.Channels;

public sealed record SendResult(
    SendStatus Status,
    string? ProviderId,
    string? Error)
{
    public static SendResult Sent(string? providerId) => new(SendStatus.Sent, providerId, null);

    public static SendResult Retry(string error) => new(SendStatus.Retry, null, error);

    public static SendResult Failed(string error) => new(SendStatus.Failed, null, error);
}
