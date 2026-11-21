using System.Text.Json;

namespace AlertApi.Application.Runs;

// Params with every {{path}} replaced. Missing lists the paths the alert had no scalar value for: such a node is
// blocked and never runs with an empty value (runbook spec §5).
public sealed record FilledParams(
    JsonElement? Params,
    IReadOnlyList<string> Missing);
