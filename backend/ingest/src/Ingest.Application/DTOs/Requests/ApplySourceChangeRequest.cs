using Ingest.Domain.Models;
using Ingest.Domain.ValueObjects;

namespace Ingest.Application.DTOs.Requests;

public sealed record ApplySourceChangeRequest(
    string Id,
    SourceKind Kind,
    SourceConfig Config,
    bool Enabled,
    DateTimeOffset UpdatedAt);
