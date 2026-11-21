using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Notifier.MockGateway.DTOs.Requests;

// Same body for SMS and call; Text is the SMS text or the text-to-speech script.
public sealed record SendMessageRequest(
    [Required][property: JsonPropertyName("notification_id")] string NotificationId,
    [Required][property: JsonPropertyName("to")] string To,
    [Required][property: JsonPropertyName("text")] string Text);
