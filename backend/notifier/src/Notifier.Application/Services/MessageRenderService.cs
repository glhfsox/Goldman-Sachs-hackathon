using System.Text;

using Microsoft.Extensions.Options;

using Notifier.Application.DTOs.Channels;
using Notifier.Application.Options;
using Notifier.Domain.Models;
using Notifier.Domain.ValueObjects;

namespace Notifier.Application.Services;

// Per-channel shape from contracts/runbooks.md §1: UI and email get every runbook step, SMS is the title
// plus the short link in 160 characters, a call is the title and the first step as text-to-speech.
public sealed class MessageRenderService : IMessageRenderService
{
    public const int SmsLimit = 160;

    private readonly DeliveryOptions _options;

    public MessageRenderService(IOptions<DeliveryOptions> options)
    {
        _options = options.Value;
    }

    public ChannelMessage Render(Notification notification, int foldedCount)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var content = notification.Content;
        var link = $"{_options.PublicBaseUrl.TrimEnd('/')}/a/{notification.AlertId}";
        var title = foldedCount > 0 ? $"{content.Title} (and {foldedCount} more)" : content.Title;
        var body = notification.Channel switch
        {
            "sms" => RenderSms(content.Severity, title, link),
            "call" => RenderCall(title, content),
            _ => RenderFull(content, link, notification.Reason)
        };

        return new ChannelMessage(
            notification.Id,
            notification.Channel,
            notification.AlertId,
            content.Severity,
            title,
            body,
            link);
    }

    // The link is what lets the person act, so the title is shortened and the link never is.
    private static string RenderSms(string severity, string title, string link)
    {
        var prefix = $"[{severity.ToUpperInvariant()}] ";
        var room = SmsLimit - prefix.Length - link.Length - 1;
        var shortTitle = title.Length <= room
            ? title
            : string.Concat(title.AsSpan(0, Math.Max(0, room - 3)), "...");

        return $"{prefix}{shortTitle} {link}";
    }

    private static string RenderCall(string title, NotificationContent content)
    {
        var firstStep = content.RunbookSteps.FirstOrDefault();
        if (firstStep is null)
        {
            return $"Alert. {title}. {content.Body}";
        }

        return $"Alert. {title}. First step: {firstStep}";
    }

    private static string RenderFull(NotificationContent content, string link, string reason)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(content.Body))
        {
            builder.AppendLine(content.Body).AppendLine();
        }

        foreach (var field in content.Fields)
        {
            builder.AppendLine($"{field.Key}: {field.Value}");
        }

        if (content.RunbookSteps.Count > 0)
        {
            builder.AppendLine().AppendLine($"Runbook: {content.RunbookTitle ?? "next steps"}");
            for (var index = 0; index < content.RunbookSteps.Count; index++)
            {
                builder.AppendLine($"{index + 1}. {content.RunbookSteps[index]}");
            }
        }

        builder.AppendLine().AppendLine($"Open the alert: {link}");
        builder.Append($"Why you got this: {reason}");

        return builder.ToString();
    }
}
