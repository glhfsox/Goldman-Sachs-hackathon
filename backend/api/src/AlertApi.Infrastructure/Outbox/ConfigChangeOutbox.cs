using System.Text.Json;
using System.Text.Json.Serialization;

using AlertApi.Application.Interfaces;
using AlertApi.Domain.Models;
using AlertApi.Infrastructure.Data;

namespace AlertApi.Infrastructure.Outbox;

public sealed class ConfigChangeOutbox : IConfigChangeOutbox
{
    public const string Topic = "config.changes";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ApiDbContext _context;
    private readonly TimeProvider _timeProvider;

    public ConfigChangeOutbox(ApiDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    // The record format of contracts/events.md section 6; the key makes the compacted topic keep the latest per row.
    public void Add(string kind, string id, DateTimeOffset updatedAt, object body)
    {
        var payload = JsonSerializer.Serialize(
            new
            {
                Kind = kind,
                Id = id,
                UpdatedAt = updatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                Deleted = false,
                Body = body
            },
            JsonOptions);

        _context.Outbox.Add(OutboxMessage.Create(Topic, $"{kind}:{id}", payload, _timeProvider.GetUtcNow()));
    }
}
