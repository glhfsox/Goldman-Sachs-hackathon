using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AlertApi.Application.Exceptions;
using AlertApi.Application.Validation;
using AlertApi.Domain.Models;

namespace AlertApi.Application.Actions;

public sealed record ResolvedAction(
    ActionRequest Request,
    Risk Risk,
    string TargetId,
    int TargetVersion);

// Turns a step's action and params into the request a plugin sends. The host always comes from a registered target:
// a step names a target (or gets the action's default) or gives a URL whose origin a target lists. Values from the
// alert can only fill path segments, escaped, never choose the host.
public static partial class ActionTargetResolver
{
    public const string TargetParam = "target";
    public const string OperationParam = "operation";
    public const string UrlParam = "url";

    private static readonly string[] Reserved = [TargetParam, OperationParam, UrlParam];

    public static ResolvedAction? Resolve(
        ActionDescriptor action,
        JsonElement? parameters,
        IReadOnlyDictionary<string, TargetDefinition> targets,
        string path,
        List<ProblemItem> problems)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(problems);

        var values = parameters is { ValueKind: JsonValueKind.Object } element
            ? element.EnumerateObject().ToDictionary(property => property.Name, property => property.Value)
            : [];
        var paramsPath = JsonChecks.Join(path, "params");

        if (values.TryGetValue(UrlParam, out var url))
        {
            if (values.ContainsKey(TargetParam))
            {
                problems.Add(new ProblemItem(paramsPath, "give either url or target, not both"));

                return null;
            }

            return ResolveUrl(action, url, values, targets, JsonChecks.Join(paramsPath, UrlParam), problems);
        }

        return ResolveOperation(action, values, targets, paramsPath, problems);
    }

    private static ResolvedAction? ResolveUrl(
        ActionDescriptor action,
        JsonElement url,
        Dictionary<string, JsonElement> values,
        IReadOnlyDictionary<string, TargetDefinition> targets,
        string urlPath,
        List<ProblemItem> problems)
    {
        var text = url.ValueKind == JsonValueKind.String ? url.GetString()! : string.Empty;
        var match = OriginPattern().Match(text);

        if (!match.Success || match.Groups["origin"].Value.Contains("{{", StringComparison.Ordinal))
        {
            problems.Add(new ProblemItem(urlPath, "must be an http(s) URL whose host is written out, not a placeholder"));

            return null;
        }

        var origin = match.Groups["origin"].Value;
        var target = targets.Values.FirstOrDefault(candidate =>
            candidate.Origins.Any(known => string.Equals(known.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase)));

        if (target is null)
        {
            problems.Add(new ProblemItem(urlPath, $"'{origin}' is not the origin of a registered action target"));

            return null;
        }

        var rest = match.Groups["rest"].Value;
        var request = new ActionRequest(
            action.UrlMethod,
            target.BaseUrl.TrimEnd('/') + (rest.Length == 0 ? "/" : rest),
            BodyOf(action.UrlMethod, values, consumed: []));

        return new ResolvedAction(request, action.Risk, target.Id, target.Version);
    }

    private static ResolvedAction? ResolveOperation(
        ActionDescriptor action,
        Dictionary<string, JsonElement> values,
        IReadOnlyDictionary<string, TargetDefinition> targets,
        string paramsPath,
        List<ProblemItem> problems)
    {
        var targetId = StringParam(values, TargetParam) ?? action.DefaultTarget;

        if (targetId is null)
        {
            problems.Add(new ProblemItem(paramsPath, $"action '{action.Name}' needs a target or a url"));

            return null;
        }

        if (!targets.TryGetValue(targetId, out var target))
        {
            problems.Add(new ProblemItem(JsonChecks.Join(paramsPath, TargetParam), $"'{targetId}' is not a registered action target"));

            return null;
        }

        var operationName = StringParam(values, OperationParam) ?? action.Name;

        if (!target.Operations.TryGetValue(operationName, out var operation))
        {
            problems.Add(new ProblemItem(
                JsonChecks.Join(paramsPath, OperationParam),
                $"target '{target.Id}' has no operation '{operationName}' (it has: {string.Join(", ", target.Operations.Keys)})"));

            return null;
        }

        var consumed = new HashSet<string>();
        var missing = false;
        var filledPath = PathParamPattern().Replace(operation.Path, segment =>
        {
            var name = segment.Groups[1].Value;
            consumed.Add(name);

            if (values.TryGetValue(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                return Uri.EscapeDataString(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText());
            }

            problems.Add(new ProblemItem(JsonChecks.Join(paramsPath, name), $"is required by {target.Id}.{operationName} ({operation.Path})"));
            missing = true;

            return segment.Value;
        });

        if (missing)
        {
            return null;
        }

        var request = new ActionRequest(
            operation.Method,
            target.BaseUrl.TrimEnd('/') + filledPath,
            BodyOf(operation.Method, values, consumed));

        return new ResolvedAction(request, RiskNames.Max(action.Risk, operation.Risk ?? action.Risk), target.Id, target.Version);
    }

    // Params that are not routing or path values travel in the body (e.g. escalate's team); a GET has none.
    private static string? BodyOf(string method, Dictionary<string, JsonElement> values, HashSet<string> consumed)
    {
        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var body = new JsonObject();

        foreach (var (name, value) in values.Where(pair => !Reserved.Contains(pair.Key) && !consumed.Contains(pair.Key)))
        {
            body[name] = JsonNode.Parse(value.GetRawText());
        }

        return body.Count == 0 ? null : body.ToJsonString();
    }

    private static string? StringParam(Dictionary<string, JsonElement> values, string name)
    {
        return values.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    [GeneratedRegex(@"^(?<origin>https?://[^/?#]+)(?<rest>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex OriginPattern();

    [GeneratedRegex(@"\{([A-Za-z0-9_]+)\}")]
    private static partial Regex PathParamPattern();
}
