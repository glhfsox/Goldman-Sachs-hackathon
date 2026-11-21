using System.Text.Json;

using AlertApi.Domain.Models;

namespace AlertApi.Application.DTOs.Responses;

public sealed record ActionTargetResponse(
    string Id,
    string BaseUrl,
    JsonElement Origins,
    JsonElement Operations,
    string? SecretRef,
    int Version,
    DateTimeOffset UpdatedAt)
{
    public static ActionTargetResponse From(ActionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return new ActionTargetResponse(
            target.Id,
            target.BaseUrl,
            Parse(target.Origins),
            Parse(target.Operations),
            target.SecretRef,
            target.Version,
            target.UpdatedAt);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }
}
