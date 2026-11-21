namespace Notifier.Infrastructure.Channels;

public sealed class ChannelOptions
{
    public const string SectionName = "Channels";

    // Only adapters listed here are used; registering an adapter without listing it keeps it off.
    public string[] Enabled { get; init; } = [];

    public string SmtpHost { get; init; } = "localhost";

    public int SmtpPort { get; init; } = 1025;

    public string EmailFrom { get; init; } = "Alerts <alerts@notifier.local>";

    public string SmsGatewayUrl { get; init; } = "http://localhost:8002/sms";

    public string CallGatewayUrl { get; init; } = "http://localhost:8002/call";

    public int GatewayTimeoutSeconds { get; init; } = 2;

    // Open SMTP connections kept between emails; also the most emails sent at the same time.
    public int SmtpPoolSize { get; init; } = 4;

    // Connect, each SMTP command, and the wait for a free pooled connection. Must stay well under
    // Delivery:LeaseSeconds, or a slow send outlives its lease and another replica sends it again.
    public int SmtpTimeoutSeconds { get; init; } = 5;
}
