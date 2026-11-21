using System.Text.Json;

namespace AlertApi.Application.DTOs.Requests;

public sealed record UpdateRunbookRequest(
    JsonElement? Canonical,
    string? Status);
