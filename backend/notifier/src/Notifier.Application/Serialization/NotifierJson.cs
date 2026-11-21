using System.Text.Json;
using System.Text.Json.Serialization;

namespace Notifier.Application.Serialization;

// Every contract record (events.md, api.md) is snake_case JSON.
public static class NotifierJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
}
