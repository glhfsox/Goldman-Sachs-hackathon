using System.Text.Json;

using AlertApi.Application.Serialization;
using AlertApi.Domain.Models;

namespace AlertApi.Application.DTOs.Responses;

public sealed record AlertEventResponse(
    string Id,
    string AlertId,
    DateTimeOffset At,
    string Actor,
    string Kind,
    JsonElement Data)
{
    public static AlertEventResponse From(AlertEvent alertEvent)
    {
        ArgumentNullException.ThrowIfNull(alertEvent);

        return new AlertEventResponse(
            alertEvent.Id,
            alertEvent.AlertId,
            alertEvent.At,
            alertEvent.Actor,
            alertEvent.Kind,
            RecordJson.ToElement(alertEvent.Data));
    }
}
