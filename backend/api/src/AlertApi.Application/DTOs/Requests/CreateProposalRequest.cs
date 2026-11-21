using System.Text.Json;

namespace AlertApi.Application.DTOs.Requests;

public sealed record CreateProposalRequest(
    string? Kind,
    JsonElement? Body);
