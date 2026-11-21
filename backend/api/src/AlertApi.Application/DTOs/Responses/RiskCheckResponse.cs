using System.Text.Json;

using AlertApi.Domain.Models;

namespace AlertApi.Application.DTOs.Responses;

public sealed record RiskCheckResponse(
    string Verdict,
    double? Confidence,
    IReadOnlyList<string> Reasons,
    string? By,
    DateTimeOffset? At)
{
    public static RiskCheckResponse? From(RunbookRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.RiskCheckVerdict is null)
        {
            return null;
        }

        return new RiskCheckResponse(
            run.RiskCheckVerdict,
            run.RiskCheckConfidence,
            JsonSerializer.Deserialize<List<string>>(run.RiskCheckReasons ?? "[]") ?? [],
            run.RiskCheckBy,
            run.RiskCheckAt);
    }
}
