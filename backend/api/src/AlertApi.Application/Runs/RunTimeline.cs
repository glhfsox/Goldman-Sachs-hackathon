using System.Text.Json;

using AlertApi.Application.Serialization;
using AlertApi.Domain.Models;

namespace AlertApi.Application.Runs;

// The alert timeline rows that describe a run itself (not one step's attempt).
public static class RunTimeline
{
    public static AlertEvent Create(string id, RunbookRun run, string actor, string kind, DateTimeOffset at, string? node = null, IReadOnlyList<string>? reasons = null)
    {
        ArgumentNullException.ThrowIfNull(run);

        var data = JsonSerializer.Serialize(
            new { Run = new { run.Id, run.RunbookId, run.Risk, run.Trigger, run.Status, Node = node }, Reasons = reasons },
            RecordJson.Options);

        return AlertEvent.Create(id, run.AlertId, at, actor, kind, data);
    }
}
