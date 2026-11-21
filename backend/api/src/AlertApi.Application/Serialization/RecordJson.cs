using System.Text.Json;

namespace AlertApi.Application.Serialization;

public static class RecordJson
{
    // Strict on purpose: a record with a missing or null required field is rejected instead of stored half empty.
    public static readonly JsonSerializerOptions Options = Create();

    public static JsonElement ToElement(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true
        };
        options.Converters.Add(new UtcTimestampConverter());

        return options;
    }
}
