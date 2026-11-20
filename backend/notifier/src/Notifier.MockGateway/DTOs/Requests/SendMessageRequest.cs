using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Notifier.MockGateway.DTOs.Requests;

// Same body for SMS and call; Text is the SMS text or the text-to-speech script.
public sealed record SendMessageRequest(
    [property: Required, JsonPropertyName("notification_id")] string NotificationId,
    [property: Required, JsonPropertyName("to")] string To,
    [property: Required, JsonPropertyName("text")] string Text);
