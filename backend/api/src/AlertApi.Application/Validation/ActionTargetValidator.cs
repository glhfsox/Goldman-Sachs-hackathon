using System.Text.Json;
using System.Text.RegularExpressions;

using AlertApi.Application.DTOs.Requests;
using AlertApi.Application.Exceptions;
using AlertApi.Domain.Models;

namespace AlertApi.Application.Validation;

public static partial class ActionTargetValidator
{
    private static readonly string[] Methods = ["GET", "POST", "PUT", "DELETE"];

    public static List<ProblemItem> Validate(string id, PutActionTargetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var problems = new List<ProblemItem>();

        if (!IdPattern().IsMatch(id))
        {
            problems.Add(new ProblemItem("id", "must be lowercase letters, digits and '-', at most 60"));
        }

        if (!IsHttpUrl(request.BaseUrl) || request.BaseUrl!.Contains('?') || request.BaseUrl.Contains('#'))
        {
            problems.Add(new ProblemItem("base_url", "must be an absolute http(s) URL without query or fragment"));
        }

        if (request.Origins is { ValueKind: not JsonValueKind.Null } origins)
        {
            ValidateOrigins(origins, problems);
        }

        if (request.Operations is not { ValueKind: JsonValueKind.Object } operations || !operations.EnumerateObject().Any())
        {
            problems.Add(new ProblemItem("operations", "must be a non-empty object of operation name -> {method, path, risk?}"));
        }
        else
        {
            foreach (var operation in operations.EnumerateObject())
            {
                ValidateOperation(operation, problems);
            }
        }

        if (request.SecretRef is { } secretRef && string.IsNullOrWhiteSpace(secretRef))
        {
            problems.Add(new ProblemItem("secret_ref", "must be a non-empty string or left out"));
        }

        return problems;
    }

    private static void ValidateOrigins(JsonElement origins, List<ProblemItem> problems)
    {
        if (origins.ValueKind != JsonValueKind.Array)
        {
            problems.Add(new ProblemItem("origins", "must be an array of origins such as \"http://counterparty-service\""));

            return;
        }

        var index = 0;

        foreach (var origin in origins.EnumerateArray())
        {
            if (origin.ValueKind != JsonValueKind.String || !OriginPattern().IsMatch(origin.GetString()!))
            {
                problems.Add(new ProblemItem($"origins[{index}]", "must be scheme and host only, e.g. \"http://counterparty-service\""));
            }

            index++;
        }
    }

    private static void ValidateOperation(JsonProperty operation, List<ProblemItem> problems)
    {
        var path = $"operations.{operation.Name}";

        if (!OperationNamePattern().IsMatch(operation.Name))
        {
            problems.Add(new ProblemItem(path, "name must be lowercase letters, digits and '_', at most 40"));
        }

        if (!JsonChecks.RequireObject(operation.Value, path, problems, "method", "path", "risk"))
        {
            return;
        }

        if (JsonChecks.TryGetRequired(operation.Value, path, "method", problems, out var method))
        {
            JsonChecks.CheckOneOf(method, JsonChecks.Join(path, "method"), problems, Methods);
        }

        if (JsonChecks.TryGetRequired(operation.Value, path, "path", problems, out var operationPath)
            && (operationPath.ValueKind != JsonValueKind.String || !OperationPathPattern().IsMatch(operationPath.GetString()!)))
        {
            problems.Add(new ProblemItem(JsonChecks.Join(path, "path"), "must start with '/' and may hold {name} segments, e.g. /workflows/{workflowId}/retry"));
        }

        if (operation.Value.TryGetProperty("risk", out var risk))
        {
            JsonChecks.CheckOneOf(risk, JsonChecks.Join(path, "risk"), problems, RiskNames.All);
        }
    }

    private static bool IsHttpUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,59}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[a-z0-9_]{1,40}$")]
    private static partial Regex OperationNamePattern();

    [GeneratedRegex(@"^https?://[^/?#\s{}]+$", RegexOptions.IgnoreCase)]
    private static partial Regex OriginPattern();

    [GeneratedRegex(@"^/[A-Za-z0-9_\-./{}]*$")]
    private static partial Regex OperationPathPattern();
}
