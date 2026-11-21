namespace Notifier.Application.DTOs.Channels;

// Channel adapter argument "recipient" (contracts/runbooks.md §3).
public sealed record ChannelRecipient(
    string UserId,
    string Name,
    string? Email,
    string? Phone);
