using System.Text.Json;

using AlertApi.Application.Serialization;
using AlertApi.Domain.Models;

namespace AlertApi.Application.DTOs.Responses;

public sealed record ProposalResponse(
    string Id,
    string AlertId,
    string Kind,
    JsonElement Body,
    string Author,
    string Status,
    string? DecidedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt)
{
    public static ProposalResponse From(Proposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        return new ProposalResponse(
            proposal.Id,
            proposal.AlertId,
            proposal.Kind,
            RecordJson.ToElement(proposal.Body),
            proposal.Author,
            proposal.Status,
            proposal.DecidedBy,
            proposal.CreatedAt,
            proposal.DecidedAt);
    }
}
