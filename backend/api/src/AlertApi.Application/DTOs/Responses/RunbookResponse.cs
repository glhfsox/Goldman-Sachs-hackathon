using System.Text.Json;

using AlertApi.Domain.Models;

namespace AlertApi.Application.DTOs.Responses;

public sealed record RunbookResponse(
    string Id,
    string TeamId,
    string Title,
    string Format,
    string Source,
    JsonElement Canonical,
    string Status,
    int Version,
    string? AcceptedBy,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset UpdatedAt)
{
    public static RunbookResponse From(Runbook runbook)
    {
        ArgumentNullException.ThrowIfNull(runbook);

        return new RunbookResponse(
            runbook.Id,
            runbook.TeamId,
            runbook.Title,
            runbook.Format,
            runbook.Source,
            ParseCanonical(runbook.Canonical),
            runbook.Status,
            runbook.Version,
            runbook.AcceptedBy,
            runbook.AcceptedAt,
            runbook.UpdatedAt);
    }

    // A draft has no canonical form yet; it is sent as JSON null so consumers can tell it apart (events.md §6).
    private static JsonElement ParseCanonical(string? canonical)
    {
        using var document = JsonDocument.Parse(canonical ?? "null");

        return document.RootElement.Clone();
    }
}
