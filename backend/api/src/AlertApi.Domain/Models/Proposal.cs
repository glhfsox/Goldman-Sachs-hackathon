namespace AlertApi.Domain.Models;

// A suggestion about an alert's rule or runbook, from a person or the ai-worker; a person accepts or rejects it.
public class Proposal
{
    public const string Open = "open";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";

    private Proposal()
    {
    }

    public string Id { get; private set; } = null!;

    public string AlertId { get; private set; } = null!;

    public string Kind { get; private set; } = null!;

    public string Body { get; private set; } = null!;

    public string Author { get; private set; } = null!;

    public string Status { get; private set; } = null!;

    // Set by a client that retries (the ai-worker sends its job id); one key files one proposal.
    public string? IdempotencyKey { get; private set; }

    public string? DecidedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public static Proposal Create(string id, string alertId, string kind, string body, string author, DateTimeOffset createdAt, string? idempotencyKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(alertId);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(author);

        return new Proposal
        {
            Id = id,
            AlertId = alertId,
            Kind = kind,
            Body = body,
            Author = author,
            Status = Open,
            IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey,
            CreatedAt = createdAt
        };
    }

    public void Decide(bool accepted, string userId, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        if (Status != Open)
        {
            throw new InvalidOperationException($"Proposal '{Id}' is already {Status}.");
        }

        Status = accepted ? Accepted : Rejected;
        DecidedBy = userId;
        DecidedAt = at;
    }
}
