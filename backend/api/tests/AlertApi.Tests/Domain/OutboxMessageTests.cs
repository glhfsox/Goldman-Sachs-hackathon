using AlertApi.Domain.Models;

namespace AlertApi.Tests.Domain;

public sealed class OutboxMessageTests
{
    [Fact]
    public void Create_WithValidValues_KeepsThem()
    {
        var createdAt = DateTimeOffset.Parse("2026-11-21T10:00:00Z");

        var message = OutboxMessage.Create("config.changes", "rule:r1", "{\"kind\":\"rule\"}", createdAt);

        Assert.Equal("config.changes", message.Topic);
        Assert.Equal("rule:r1", message.Key);
        Assert.Equal("{\"kind\":\"rule\"}", message.Payload);
        Assert.Equal(createdAt, message.CreatedAt);
    }

    [Theory]
    [InlineData("", "key", "{}")]
    [InlineData("topic", " ", "{}")]
    [InlineData("topic", "key", "")]
    public void Create_WithBlankValue_Throws(string topic, string key, string payload)
    {
        Assert.ThrowsAny<ArgumentException>(() => OutboxMessage.Create(topic, key, payload, DateTimeOffset.UtcNow));
    }
}
