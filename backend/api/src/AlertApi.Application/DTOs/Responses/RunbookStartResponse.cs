using AlertApi.Domain.Models;

namespace AlertApi.Application.DTOs.Responses;

public sealed record RunbookStartResponse(
    string RuleId,
    string RunbookId,
    string Start,
    string SetBy,
    DateTimeOffset UpdatedAt)
{
    public static RunbookStartResponse From(RunbookStart start)
    {
        ArgumentNullException.ThrowIfNull(start);

        return new RunbookStartResponse(start.RuleId, start.RunbookId, start.Start, start.SetBy, start.UpdatedAt);
    }
}
