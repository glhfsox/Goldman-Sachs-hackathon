using System.Net.Sockets;

using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

using Notifier.Application.DTOs.Channels;
using Notifier.Application.Interfaces;

namespace Notifier.Infrastructure.Channels;

// Plain SMTP: MailHog in the demo; a real relay is a config change (host, port).
public sealed class EmailChannel : IChannelAdapter
{
    private readonly ChannelOptions _options;

    public EmailChannel(IOptions<ChannelOptions> options)
    {
        _options = options.Value;
    }

    public string Channel => "email";

    public async Task<SendResult> SendAsync(ChannelRecipient recipient, ChannelMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(message);

        if (string.IsNullOrWhiteSpace(recipient.Email) || !MailboxAddress.TryParse(recipient.Email, out var to))
        {
            return SendResult.Failed($"User '{recipient.UserId}' has no valid email address.");
        }

        to.Name = recipient.Name;
        var mail = new MimeMessage
        {
            Subject = $"[{message.Severity.ToUpperInvariant()}] {message.Title}",
            MessageId = $"{message.NotificationId}@notifier",
            Body = new TextPart("plain") { Text = message.Body }
        };
        mail.From.Add(MailboxAddress.Parse(_options.EmailFrom));
        mail.To.Add(to);

        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(_options.SmtpHost, _options.SmtpPort, SecureSocketOptions.None, cancellationToken);
            await client.SendAsync(new MailKit.SendRequest(mail), cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception exception) when (exception is SocketException or IOException or SmtpProtocolException
            or SmtpCommandException or MailKit.ServiceNotConnectedException or TimeoutException)
        {
            return SendResult.Retry($"SMTP {_options.SmtpHost}:{_options.SmtpPort} is unavailable: {exception.Message}");
        }

        return SendResult.Sent(mail.MessageId);
    }
}
