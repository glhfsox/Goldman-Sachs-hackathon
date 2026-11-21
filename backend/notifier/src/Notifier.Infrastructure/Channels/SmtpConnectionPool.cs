using System.Collections.Concurrent;

using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;

namespace Notifier.Infrastructure.Channels;

// Keeps SMTP connections open between emails: the connect, greeting and EHLO (and, with a real relay, TLS
// and AUTH) happen once per connection instead of once per message. SmtpClient is not thread-safe, so each
// connection is used by one sender at a time and at most SmtpPoolSize are open.
public sealed class SmtpConnectionPool : IDisposable
{
    // Servers drop idle connections (often after about a minute); an older one is replaced, not reused.
    private static readonly TimeSpan MaxIdle = TimeSpan.FromSeconds(30);

    private readonly ConcurrentBag<(SmtpClient Client, DateTime ReturnedAt)> _idle = [];
    private readonly SemaphoreSlim _slots;
    private readonly ChannelOptions _options;

    public SmtpConnectionPool(IOptions<ChannelOptions> options)
    {
        _options = options.Value;
        _slots = new SemaphoreSlim(Math.Max(1, _options.SmtpPoolSize));
    }

    // `fresh` skips idle connections: used to retry once after a reused connection turned out to be dead.
    public async Task<SmtpLease> RentAsync(bool fresh, CancellationToken cancellationToken = default)
    {
        await _slots.WaitAsync(cancellationToken);
        try
        {
            while (!fresh && _idle.TryTake(out var entry))
            {
                if (entry.Client.IsConnected && DateTime.UtcNow - entry.ReturnedAt < MaxIdle)
                {
                    return new SmtpLease(this, entry.Client, reused: true);
                }

                Close(entry.Client);
            }

            var client = new SmtpClient();
            try
            {
                await client.ConnectAsync(_options.SmtpHost, _options.SmtpPort, SecureSocketOptions.None, cancellationToken);
            }
            catch
            {
                client.Dispose();
                throw;
            }

            return new SmtpLease(this, client, reused: false);
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    public void Dispose()
    {
        while (_idle.TryTake(out var entry))
        {
            Close(entry.Client);
        }

        _slots.Dispose();
    }

    internal void Return(SmtpClient client, bool healthy)
    {
        if (healthy && client.IsConnected)
        {
            _idle.Add((client, DateTime.UtcNow));
        }
        else
        {
            Close(client);
        }

        _slots.Release();
    }

    private static void Close(SmtpClient client)
    {
        try
        {
            if (client.IsConnected)
            {
                client.Disconnect(true);
            }
        }
        catch (Exception)
        {
            // Already broken; disposing is all that is left.
        }

        client.Dispose();
    }
}

public sealed class SmtpLease : IDisposable
{
    private readonly SmtpConnectionPool _pool;
    private bool _broken;
    private bool _returned;

    internal SmtpLease(SmtpConnectionPool pool, SmtpClient client, bool reused)
    {
        _pool = pool;
        Client = client;
        Reused = reused;
    }

    public SmtpClient Client { get; }

    // True when the connection was used before: a failure may only mean the server closed it meanwhile.
    public bool Reused { get; }

    public void MarkBroken() => _broken = true;

    public void Dispose()
    {
        if (_returned)
        {
            return;
        }

        _returned = true;
        _pool.Return(Client, !_broken);
    }
}
