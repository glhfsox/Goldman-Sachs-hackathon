namespace AlertApi.Domain.Models;

// A Kafka record waiting to be published. It is written in the same transaction as the change it describes.
public class OutboxMessage
{
    private OutboxMessage()
    {
    }

    // The id is database-generated: records must leave in the order they were written (contracts/schemas/api.md).
    public long Id { get; private set; }

    public string Topic { get; private set; } = null!;

    public string Key { get; private set; } = null!;

    public string Payload { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }

    public static OutboxMessage Create(string topic, string key, string payload, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        return new OutboxMessage
        {
            Topic = topic,
            Key = key,
            Payload = payload,
            CreatedAt = createdAt
        };
    }
}
