namespace Notifier.Domain.Models;

public class OutboxMessage
{
    private OutboxMessage()
    {
    }

    public Guid Id { get; private set; }

    // Assigned by the database; the dispatcher publishes in this order, so the records of one alert
    // reach Kafka in the order they were written.
    public long Sequence { get; private set; }

    public string Topic { get; private set; } = null!;

    public string Key { get; private set; } = null!;

    public string Payload { get; private set; } = null!;

    public DateTime CreatedAt { get; private set; }

    public DateTime? ProcessedAt { get; private set; }

    public int Attempts { get; private set; }

    public string? Error { get; private set; }

    public static OutboxMessage Create(string topic, string key, string payload, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        return new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Topic = topic,
            Key = key,
            Payload = payload,
            CreatedAt = now
        };
    }

    public void MarkProcessed(DateTime now)
    {
        Attempts++;
        ProcessedAt = now;
        Error = null;
    }

    // After the last attempt the message is closed with its error kept, so one broken target cannot block the outbox.
    public void MarkFailed(string error, int maxAttempts, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        Attempts++;
        Error = error;
        if (Attempts >= maxAttempts)
        {
            ProcessedAt = now;
        }
    }
}
