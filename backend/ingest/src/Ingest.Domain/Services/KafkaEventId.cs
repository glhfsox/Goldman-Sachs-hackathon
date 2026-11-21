using System.Security.Cryptography;
using System.Text;

namespace Ingest.Domain.Services;

public static class KafkaEventId
{
    private static readonly Guid Namespace = new("6f1c2b7e-3a45-4d8e-9b0a-5c7e2d4f8a11");

    // UUID v5 of source, topic, partition and offset: reading the same message again (a replay
    // after a crash) gives the same event_id, so consumers can drop the repeat.
    public static Guid Create(string sourceId, string topic, int partition, long offset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        var name = Encoding.UTF8.GetBytes($"{sourceId}/{topic}/{partition}/{offset}");
        var input = Namespace.ToByteArray(bigEndian: true).Concat(name).ToArray();
        var bytes = SHA1.HashData(input)[..16];

        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes, bigEndian: true);
    }
}
