using System.Text.Json;

using AlertApi.Application.Serialization;

namespace AlertApi.Tests.Serialization;

public sealed class UtcTimestampConverterTests
{
    [Fact]
    public void Write_UsesUtcWithMilliseconds()
    {
        var value = new DateTimeOffset(2026, 11, 20, 11, 15, 2, 180, TimeSpan.FromHours(1));

        var json = JsonSerializer.Serialize(value, RecordJson.Options);

        Assert.Equal("\"2026-11-20T10:15:02.180Z\"", json);
    }

    [Fact]
    public void Read_AcceptsTheContractFormatAndAnOffset()
    {
        var utc = JsonSerializer.Deserialize<DateTimeOffset>("\"2026-11-20T10:15:02.123Z\"", RecordJson.Options);
        var offset = JsonSerializer.Deserialize<DateTimeOffset>("\"2026-11-20T11:15:02.123+01:00\"", RecordJson.Options);

        Assert.Equal(utc, offset);
        Assert.Equal(TimeSpan.Zero, offset.Offset);
    }

    [Fact]
    public void Read_RejectsText_ThatIsNotATimestamp()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DateTimeOffset>("\"soon\"", RecordJson.Options));
    }
}
