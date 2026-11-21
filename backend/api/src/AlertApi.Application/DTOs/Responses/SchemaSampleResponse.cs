using System.Text.Json;

using AlertApi.Application.Serialization;
using AlertApi.Domain.Models;

namespace AlertApi.Application.DTOs.Responses;

public sealed record SchemaSampleResponse(
    string Fingerprint,
    string SourceId,
    JsonElement Sample,
    string Status,
    string? Error,
    DateTimeOffset CreatedAt)
{
    public static SchemaSampleResponse From(SchemaSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        return new SchemaSampleResponse(
            sample.Fingerprint,
            sample.SourceId,
            RecordJson.ToElement(sample.Sample),
            sample.Status,
            sample.Error,
            sample.CreatedAt);
    }
}
