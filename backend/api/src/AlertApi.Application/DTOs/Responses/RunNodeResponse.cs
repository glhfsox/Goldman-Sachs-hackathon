using System.Text.Json;

using AlertApi.Application.Serialization;
using AlertApi.Domain.Models;

namespace AlertApi.Application.DTOs.Responses;

public sealed record RunNodeResponse(
    string Id,
    int StepIndex,
    string Text,
    string? Action,
    string Risk,
    JsonElement? Params,
    IReadOnlyList<string> Needs,
    string Status,
    int Attempt,
    JsonElement? Result,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt)
{
    public static RunNodeResponse From(ActionRun node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return new RunNodeResponse(
            node.NodeId,
            node.StepIndex,
            node.Text,
            node.Action,
            node.Risk,
            node.Params is null ? null : RecordJson.ToElement(node.Params),
            JsonSerializer.Deserialize<List<string>>(node.Needs) ?? [],
            node.Status,
            node.Attempt,
            node.Result is null ? null : RecordJson.ToElement(node.Result),
            node.StartedAt,
            node.FinishedAt);
    }
}
