using System.Net.Sockets;

using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;
using MimeKit;

using Notifier.Application.DTOs.Channels;
using Notifier.Application.Interfaces;

namespace Notifier.Infrastructure.Channels;

// Plain SMTP: MailHog in the demo; a real relay is a config change (host, port).
// Connections come from SmtpConnectionPool and stay open between messages.
public sealed class EmailChannel : IChannelAdapter
{
    private readonly SmtpConnectionPool _pool;
    private readonly ChannelOptions _options;

    public EmailChannel(
        SmtpConnectionPool pool,
        IOptions<ChannelOptions> options)
    {
        _pool = pool;
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
            if (!await TrySendAsync(mail, fresh: false, cancellationToken))
            {
                // The server closed a pooled connection while it was idle: once more on a new one.
                await TrySendAsync(mail, fresh: true, cancellationToken);
            }
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            return SendResult.Retry($"SMTP {_options.SmtpHost}:{_options.SmtpPort} is unavailable: {exception.Message}");
        }

        return SendResult.Sent(mail.MessageId);
    }

    // False when a reused connection turned out to be dead; any other failure is thrown.
    private async Task<bool> TrySendAsync(MimeMessage mail, bool fresh, CancellationToken cancellationToken)
    {
        using var lease = await _pool.RentAsync(fresh, cancellationToken);
        try
        {
            await lease.Client.SendAsync(new MailKit.SendRequest(mail), cancellationToken);

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // After any failure the session state is unknown, so the connection is not reused.
            lease.MarkBroken();
            if (lease.Reused && IsDeadConnection(exception))
            {
                return false;
            }

            throw;
        }
    }

    private static bool IsDeadConnection(Exception exception) =>
        exception is SocketException or IOException or SmtpProtocolException or MailKit.ServiceNotConnectedException;

    private static bool IsTransient(Exception exception) =>
        IsDeadConnection(exception) || exception is SmtpCommandException or TimeoutException;
}
