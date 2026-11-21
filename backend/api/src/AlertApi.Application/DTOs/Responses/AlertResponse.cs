using System.Text.Json;

using AlertApi.Application.Serialization;
using AlertApi.Domain.Models;

namespace AlertApi.Application.DTOs.Responses;

public sealed record AlertResponse(
    string Id,
    string RuleId,
    string TeamId,
    string? IncidentId,
    string Status,
    string Severity,
    string Title,
    string Body,
    JsonElement Fields,
    string? Color,
    long Count,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    DateTimeOffset FirstReceivedAt,
    string? RunbookId,
    string? AckedBy,
    string? ResolvedBy,
    JsonElement SampleEvent)
{
    public static AlertResponse From(Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        return new AlertResponse(
            alert.Id,
            alert.RuleId,
            alert.TeamId,
            alert.IncidentId,
            alert.Status,
            alert.Severity,
            alert.Title,
            alert.Body,
            RecordJson.ToElement(alert.Fields),
            alert.Color,
            alert.Count,
            alert.FirstSeen,
            alert.LastSeen,
            alert.FirstReceivedAt,
            alert.RunbookId,
            alert.AckedBy,
            alert.ResolvedBy,
            RecordJson.ToElement(alert.SampleEvent));
    }
}
