using System.Text.Json;

using AlertApi.Domain.Models;

namespace AlertApi.Application.Actions;

public sealed record OperationDefinition(
    string Method,
    string Path,
    Risk? Risk);

public sealed record TargetDefinition(
    string Id,
    string BaseUrl,
    IReadOnlyList<string> Origins,
    IReadOnlyDictionary<string, OperationDefinition> Operations,
    int Version)
{
    // Rows are validated when they are saved; a field that does not parse is left out rather than thrown.
    public static TargetDefinition From(ActionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        using var origins = JsonDocument.Parse(target.Origins);
        using var operations = JsonDocument.Parse(target.Operations);

        return new TargetDefinition(
            target.Id,
            target.BaseUrl,
            origins.RootElement.ValueKind == JsonValueKind.Array
                ? origins.RootElement.EnumerateArray().Where(origin => origin.ValueKind == JsonValueKind.String)
                    .Select(origin => origin.GetString()!).ToList()
                : [],
            ParseOperations(operations.RootElement),
            target.Version);
    }

    public static Dictionary<string, OperationDefinition> ParseOperations(JsonElement operations)
    {
        var result = new Dictionary<string, OperationDefinition>();

        if (operations.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var operation in operations.EnumerateObject())
        {
            var method = operation.Value.TryGetProperty("method", out var methodValue) ? methodValue.GetString() : null;
            var path = operation.Value.TryGetProperty("path", out var pathValue) ? pathValue.GetString() : null;
            var risk = operation.Value.TryGetProperty("risk", out var riskValue) && riskValue.ValueKind == JsonValueKind.String
                ? RiskNames.Parse(riskValue.GetString())
                : null;

            if (method is not null && path is not null)
            {
                result[operation.Name] = new OperationDefinition(method, path, risk);
            }
        }

        return result;
    }
}
