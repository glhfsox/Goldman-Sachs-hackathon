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

    public int GatewayTimeoutSeconds { get; init; } = 5;
}
